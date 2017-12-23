﻿// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : CompactDisc.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Core algorithms.
//
// --[ Description ] ----------------------------------------------------------
//
//     Dumps CDs and DDCDs.
//
// --[ License ] --------------------------------------------------------------
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU General Public License as
//     published by the Free Software Foundation, either version 3 of the
//     License, or (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//
//     You should have received a copy of the GNU General Public License
//     along with this program.  If not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2018 Natalia Portillo
// ****************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using DiscImageChef.Console;
using DiscImageChef.Core.Logging;
using DiscImageChef.Decoders.CD;
using DiscImageChef.Decoders.SCSI;
using DiscImageChef.Decoders.SCSI.MMC;
using DiscImageChef.Devices;
using DiscImageChef.DiscImages;
using DiscImageChef.Metadata;
using Extents;
using Schemas;
using MediaType = DiscImageChef.CommonTypes.MediaType;
using PlatformID = DiscImageChef.Interop.PlatformID;
using Session = DiscImageChef.Decoders.CD.Session;
using TrackType = Schemas.TrackType;

namespace DiscImageChef.Core.Devices.Dumping
{
    /// <summary>
    ///     Implement dumping Compact Discs
    /// </summary>
    class CompactDisc
    {
        /// <summary>
        ///     Dumps a compact disc
        /// </summary>
        /// <param name="dev">Device</param>
        /// <param name="devicePath">Path to the device</param>
        /// <param name="outputPrefix">Prefix for output data files</param>
        /// <param name="retryPasses">How many times to retry</param>
        /// <param name="force">Force to continue dump whenever possible</param>
        /// <param name="dumpRaw">Dump scrambled sectors</param>
        /// <param name="persistent">Store whatever data the drive returned on error</param>
        /// <param name="stopOnError">Stop dump on first error</param>
        /// <param name="resume">Information for dump resuming</param>
        /// <param name="dumpLog">Dump logger</param>
        /// <param name="sidecar">Partially filled initialized sidecar</param>
        /// <param name="dskType">Disc type as detected in MMC layer</param>
        /// <param name="separateSubchannel">Write subchannel separate from main channel</param>
        /// <param name="alcohol">Alcohol disc image already initialized</param>
        /// <param name="dumpLeadIn">Try to read and dump as much Lead-in as possible</param>
        /// <exception cref="NotImplementedException">If trying to dump scrambled sectors</exception>
        /// <exception cref="InvalidOperationException">If the resume file is invalid</exception>
        /// <exception cref="ArgumentOutOfRangeException">If the track type is unknown (never)</exception>
        internal static void Dump(Device dev, string devicePath, string outputPrefix, ushort retryPasses, bool force,
                                  bool dumpRaw, bool persistent, bool stopOnError, ref CICMMetadataType sidecar,
                                  ref MediaType dskType, bool separateSubchannel, ref Resume resume,
                                  ref DumpLog dumpLog, Alcohol120 alcohol, bool dumpLeadIn)
        {
            bool sense = false;
            ulong blocks;
            // TODO: Check subchannel support
            uint blockSize;
            uint subSize;
            FullTOC.CDFullTOC? toc = null;
            DateTime start;
            DateTime end;
            double totalDuration = 0;
            double totalChkDuration = 0;
            double currentSpeed = 0;
            double maxSpeed = double.MinValue;
            double minSpeed = double.MaxValue;
            Checksum dataChk;
            bool readcd;
            uint blocksToRead = 64;
            DataFile dumpFile;
            bool aborted = false;
            System.Console.CancelKeyPress += (sender, e) => e.Cancel = aborted = true;

            // We discarded all discs that falsify a TOC before requesting a real TOC
            // No TOC, no CD (or an empty one)
            dumpLog.WriteLine("Reading full TOC");
            bool tocSense = dev.ReadRawToc(out byte[] cmdBuf, out byte[] senseBuf, 1, dev.Timeout, out _);
            if(!tocSense)
            {
                toc = FullTOC.Decode(cmdBuf);
                if(toc.HasValue)
                {
                    byte[] tmpBuf = new byte[cmdBuf.Length - 2];
                    Array.Copy(cmdBuf, 2, tmpBuf, 0, cmdBuf.Length - 2);
                    sidecar.OpticalDisc[0].TOC = new DumpType
                    {
                        Image = outputPrefix + ".toc.bin",
                        Size = tmpBuf.Length,
                        Checksums = Checksum.GetChecksums(tmpBuf).ToArray()
                    };
                    DataFile.WriteTo("SCSI Dump", sidecar.OpticalDisc[0].TOC.Image, tmpBuf);

                    // ATIP exists on blank CDs
                    dumpLog.WriteLine("Reading ATIP");
                    sense = dev.ReadAtip(out cmdBuf, out senseBuf, dev.Timeout, out _);
                    if(!sense)
                    {
                        ATIP.CDATIP? atip = ATIP.Decode(cmdBuf);
                        if(atip.HasValue)
                        {
                            // Only CD-R and CD-RW have ATIP
                            dskType = atip.Value.DiscType ? MediaType.CDRW : MediaType.CDR;

                            tmpBuf = new byte[cmdBuf.Length - 4];
                            Array.Copy(cmdBuf, 4, tmpBuf, 0, cmdBuf.Length - 4);
                            sidecar.OpticalDisc[0].ATIP = new DumpType
                            {
                                Image = outputPrefix + ".atip.bin",
                                Size = tmpBuf.Length,
                                Checksums = Checksum.GetChecksums(tmpBuf).ToArray()
                            };
                            DataFile.WriteTo("SCSI Dump", sidecar.OpticalDisc[0].TOC.Image, tmpBuf);
                        }
                    }

                    dumpLog.WriteLine("Reading Disc Information");
                    sense = dev.ReadDiscInformation(out cmdBuf, out senseBuf,
                                                    MmcDiscInformationDataTypes.DiscInformation, dev.Timeout, out _);
                    if(!sense)
                    {
                        DiscInformation.StandardDiscInformation? discInfo = DiscInformation.Decode000b(cmdBuf);
                        if(discInfo.HasValue)
                            if(dskType == MediaType.CD)
                                switch(discInfo.Value.DiscType)
                                {
                                    case 0x10:
                                        dskType = MediaType.CDI;
                                        break;
                                    case 0x20:
                                        dskType = MediaType.CDROMXA;
                                        break;
                                }
                    }

                    int sessions = 1;
                    int firstTrackLastSession = 0;

                    dumpLog.WriteLine("Reading Session Information");
                    sense = dev.ReadSessionInfo(out cmdBuf, out senseBuf, dev.Timeout, out _);
                    if(!sense)
                    {
                        Session.CDSessionInfo? session = Session.Decode(cmdBuf);
                        if(session.HasValue)
                        {
                            sessions = session.Value.LastCompleteSession;
                            firstTrackLastSession = session.Value.TrackDescriptors[0].TrackNumber;
                        }
                    }

                    if(dskType == MediaType.CD)
                    {
                        bool hasDataTrack = false;
                        bool hasAudioTrack = false;
                        bool allFirstSessionTracksAreAudio = true;
                        bool hasVideoTrack = false;

                        foreach(FullTOC.TrackDataDescriptor track in toc.Value.TrackDescriptors)
                        {
                            if(track.TNO == 1 && ((TocControl)(track.CONTROL & 0x0D) == TocControl.DataTrack ||
                                                  (TocControl)(track.CONTROL & 0x0D) == TocControl.DataTrackIncremental)
                            ) allFirstSessionTracksAreAudio &= firstTrackLastSession != 1;

                            if((TocControl)(track.CONTROL & 0x0D) == TocControl.DataTrack ||
                               (TocControl)(track.CONTROL & 0x0D) == TocControl.DataTrackIncremental)
                            {
                                hasDataTrack = true;
                                allFirstSessionTracksAreAudio &= track.TNO >= firstTrackLastSession;
                            }
                            else hasAudioTrack = true;

                            hasVideoTrack |= track.ADR == 4;
                        }

                        if(hasDataTrack && hasAudioTrack && allFirstSessionTracksAreAudio && sessions == 2)
                            dskType = MediaType.CDPLUS;
                        if(!hasDataTrack && hasAudioTrack && sessions == 1) dskType = MediaType.CDDA;
                        if(hasDataTrack && !hasAudioTrack && sessions == 1) dskType = MediaType.CDROM;
                        if(hasVideoTrack && !hasDataTrack && sessions == 1) dskType = MediaType.CDV;
                    }

                    dumpLog.WriteLine("Reading PMA");
                    sense = dev.ReadPma(out cmdBuf, out senseBuf, dev.Timeout, out _);
                    if(!sense)
                        if(PMA.Decode(cmdBuf).HasValue)
                        {
                            tmpBuf = new byte[cmdBuf.Length - 4];
                            Array.Copy(cmdBuf, 4, tmpBuf, 0, cmdBuf.Length - 4);
                            sidecar.OpticalDisc[0].PMA = new DumpType
                            {
                                Image = outputPrefix + ".pma.bin",
                                Size = tmpBuf.Length,
                                Checksums = Checksum.GetChecksums(tmpBuf).ToArray()
                            };
                            DataFile.WriteTo("SCSI Dump", sidecar.OpticalDisc[0].PMA.Image, tmpBuf);
                        }

                    dumpLog.WriteLine("Reading CD-Text from Lead-In");
                    sense = dev.ReadCdText(out cmdBuf, out senseBuf, dev.Timeout, out _);
                    if(!sense)
                        if(CDTextOnLeadIn.Decode(cmdBuf).HasValue)
                        {
                            tmpBuf = new byte[cmdBuf.Length - 4];
                            Array.Copy(cmdBuf, 4, tmpBuf, 0, cmdBuf.Length - 4);
                            sidecar.OpticalDisc[0].LeadInCdText = new DumpType
                            {
                                Image = outputPrefix + ".cdtext.bin",
                                Size = tmpBuf.Length,
                                Checksums = Checksum.GetChecksums(tmpBuf).ToArray()
                            };
                            DataFile.WriteTo("SCSI Dump", sidecar.OpticalDisc[0].LeadInCdText.Image, tmpBuf);
                        }
                }
            }

            // TODO: Support variable subchannel kinds
            blockSize = 2448;
            subSize = 96;
            int sectorSize;
            if(separateSubchannel) sectorSize = (int)(blockSize - subSize);
            else sectorSize = (int)blockSize;

            if(toc == null)
            {
                DicConsole.ErrorWriteLine("Error trying to decode TOC...");
                return;
            }

            DiscImages.Session[] sessionsForAlcohol = new DiscImages.Session[toc.Value.LastCompleteSession];
            for(int i = 0; i < sessionsForAlcohol.Length; i++)
            {
                sessionsForAlcohol[i].SessionSequence = (ushort)(i + 1);
                sessionsForAlcohol[i].StartTrack = ushort.MaxValue;
            }
            foreach(FullTOC.TrackDataDescriptor trk in
                toc.Value.TrackDescriptors.Where(trk => trk.POINT > 0 && trk.POINT < 0xA0 &&
                                                        trk.SessionNumber <= sessionsForAlcohol.Length))
            {
                if(trk.POINT < sessionsForAlcohol[trk.SessionNumber - 1].StartTrack)
                    sessionsForAlcohol[trk.SessionNumber - 1].StartTrack = trk.POINT;
                if(trk.POINT > sessionsForAlcohol[trk.SessionNumber - 1].EndTrack)
                    sessionsForAlcohol[trk.SessionNumber - 1].EndTrack = trk.POINT;
            }

            alcohol.AddSessions(sessionsForAlcohol);

            foreach(FullTOC.TrackDataDescriptor trk in toc.Value.TrackDescriptors)
                alcohol.AddTrack((byte)((trk.ADR << 4) & trk.CONTROL), trk.TNO, trk.POINT, trk.Min, trk.Sec, trk.Frame,
                                 trk.Zero, trk.PMIN, trk.PSEC, trk.PFRAME, trk.SessionNumber);

            FullTOC.TrackDataDescriptor[] sortedTracks =
                toc.Value.TrackDescriptors.OrderBy(track => track.POINT).ToArray();
            List<TrackType> trackList = new List<TrackType>();
            long lastSector = 0;
            string lastMsf = null;
            foreach(FullTOC.TrackDataDescriptor trk in sortedTracks.Where(trk => trk.ADR == 1 || trk.ADR == 4))
                if(trk.POINT >= 0x01 && trk.POINT <= 0x63)
                {
                    TrackType track = new TrackType
                    {
                        Sequence = new TrackSequenceType {Session = trk.SessionNumber, TrackNumber = trk.POINT}
                    };
                    if((TocControl)(trk.CONTROL & 0x0D) == TocControl.DataTrack ||
                       (TocControl)(trk.CONTROL & 0x0D) == TocControl.DataTrackIncremental)
                        track.TrackType1 = TrackTypeTrackType.mode1;
                    else track.TrackType1 = TrackTypeTrackType.audio;
                    if(trk.PHOUR > 0)
                        track.StartMSF = string.Format("{3:D2}:{0:D2}:{1:D2}:{2:D2}", trk.PMIN, trk.PSEC, trk.PFRAME,
                                                       trk.PHOUR);
                    else track.StartMSF = $"{trk.PMIN:D2}:{trk.PSEC:D2}:{trk.PFRAME:D2}";
                    track.StartSector = trk.PHOUR * 3600 * 75 + trk.PMIN * 60 * 75 + trk.PSEC * 75 + trk.PFRAME - 150;
                    trackList.Add(track);
                }
                else if(trk.POINT == 0xA2)
                {
                    int phour, pmin, psec, pframe;
                    if(trk.PFRAME == 0)
                    {
                        pframe = 74;

                        if(trk.PSEC == 0)
                        {
                            psec = 59;

                            if(trk.PMIN == 0)
                            {
                                pmin = 59;
                                phour = trk.PHOUR - 1;
                            }
                            else
                            {
                                pmin = trk.PMIN - 1;
                                phour = trk.PHOUR;
                            }
                        }
                        else
                        {
                            psec = trk.PSEC - 1;
                            pmin = trk.PMIN;
                            phour = trk.PHOUR;
                        }
                    }
                    else
                    {
                        pframe = trk.PFRAME - 1;
                        psec = trk.PSEC;
                        pmin = trk.PMIN;
                        phour = trk.PHOUR;
                    }

                    lastMsf = phour > 0
                                  ? $"{phour:D2}:{pmin:D2}:{psec:D2}:{pframe:D2}"
                                  : $"{pmin:D2}:{psec:D2}:{pframe:D2}";
                    lastSector = phour * 3600 * 75 + pmin * 60 * 75 + psec * 75 + pframe - 150;
                }

            TrackType[] tracks = trackList.ToArray();
            for(int t = 1; t < tracks.Length; t++)
            {
                tracks[t - 1].EndSector = tracks[t].StartSector - 1;
                int phour = 0, pmin = 0, psec = 0;
                int pframe = (int)(tracks[t - 1].EndSector + 150);

                if(pframe > 3600 * 75)
                {
                    phour = pframe / (3600 * 75);
                    pframe -= phour * 3600 * 75;
                }
                if(pframe > 60 * 75)
                {
                    pmin = pframe / (60 * 75);
                    pframe -= pmin * 60 * 75;
                }
                if(pframe > 75)
                {
                    psec = pframe / 75;
                    pframe -= psec * 75;
                }

                tracks[t - 1].EndMSF = phour > 0
                                           ? $"{phour:D2}:{pmin:D2}:{psec:D2}:{pframe:D2}"
                                           : $"{pmin:D2}:{psec:D2}:{pframe:D2}";
            }

            tracks[tracks.Length - 1].EndMSF = lastMsf;
            tracks[tracks.Length - 1].EndSector = lastSector;
            blocks = (ulong)(lastSector + 1);

            if(blocks == 0)
            {
                DicConsole.ErrorWriteLine("Cannot dump blank media.");
                return;
            }

            if(dumpRaw) throw new NotImplementedException("Raw CD dumping not yet implemented");
            // TODO: Check subchannel capabilities
            readcd = !dev.ReadCd(out byte[] readBuffer, out senseBuf, 0, blockSize, 1, MmcSectorTypes.AllTypes, false,
                                 false, true, MmcHeaderCodes.AllHeaders, true, true, MmcErrorField.None,
                                 MmcSubchannel.Raw, dev.Timeout, out _);

            if(readcd) DicConsole.WriteLine("Using MMC READ CD command.");

            DumpHardwareType currentTry = null;
            ExtentsULong extents = null;
            ResumeSupport.Process(true, true, blocks, dev.Manufacturer, dev.Model, dev.Serial, dev.PlatformId,
                                  ref resume, ref currentTry, ref extents);
            if(currentTry == null || extents == null)
                throw new InvalidOperationException("Could not process resume file, not continuing...");

            if(dumpLeadIn)
            {
                DicConsole.WriteLine("Trying to read Lead-In...");
                bool gotLeadIn = false;
                int leadInSectorsGood = 0, leadInSectorsTotal = 0;

                dumpFile = new DataFile(outputPrefix + ".leadin.bin");
                dataChk = new Checksum();

                readBuffer = null;

                dumpLog.WriteLine("Reading Lead-in");
                for(int leadInBlock = -150; leadInBlock < 0 && resume.NextBlock == 0; leadInBlock++)
                {
                    if(dev.PlatformId == PlatformID.FreeBSD)
                    {
                        DicConsole.DebugWriteLine("Dump-Media",
                                                  "FreeBSD panics when reading CD Lead-in, see upstream bug #224253.");
                        break;
                    }

                    if(aborted)
                    {
                        dumpLog.WriteLine("Aborted!");
                        break;
                    }

#pragma warning disable RECS0018 // Comparison of floating point numbers with equality operator
                    if(currentSpeed > maxSpeed && currentSpeed != 0) maxSpeed = currentSpeed;
                    if(currentSpeed < minSpeed && currentSpeed != 0) minSpeed = currentSpeed;
#pragma warning restore RECS0018 // Comparison of floating point numbers with equality operator

                    DicConsole.Write("\rTrying to read lead-in sector {0} ({1:F3} MiB/sec.)", leadInBlock,
                                     currentSpeed);

                    sense = dev.ReadCd(out readBuffer, out senseBuf, (uint)leadInBlock, blockSize, 1,
                                       MmcSectorTypes.AllTypes, false, false, true, MmcHeaderCodes.AllHeaders, true,
                                       true, MmcErrorField.None, MmcSubchannel.Raw, dev.Timeout,
                                       out double cmdDuration);

                    if(!sense && !dev.Error)
                    {
                        dataChk.Update(readBuffer);
                        dumpFile.Write(readBuffer);
                        gotLeadIn = true;
                        leadInSectorsGood++;
                        leadInSectorsTotal++;
                    }
                    else
                    {
                        if(gotLeadIn)
                        {
                            // Write empty data
                            dataChk.Update(new byte[blockSize]);
                            dumpFile.Write(new byte[blockSize]);
                            leadInSectorsTotal++;
                        }
                    }

                    double newSpeed = blockSize / (double)1048576 / (cmdDuration / 1000);
                    if(!double.IsInfinity(newSpeed)) currentSpeed = newSpeed;
                }

                dumpFile.Close();
                if(leadInSectorsGood > 0)
                    sidecar.OpticalDisc[0].LeadIn = new[]
                    {
                        new BorderType
                        {
                            Image = outputPrefix + ".leadin.bin",
                            Checksums = dataChk.End().ToArray(),
                            Size = leadInSectorsTotal * blockSize
                        }
                    };
                else File.Delete(outputPrefix + ".leadin.bin");

                DicConsole.WriteLine();
                DicConsole.WriteLine("Got {0} lead-in sectors.", leadInSectorsGood);
                dumpLog.WriteLine("Got {0} Lead-in sectors.", leadInSectorsGood);
            }

            while(true)
            {
                if(readcd)
                {
                    sense = dev.ReadCd(out readBuffer, out senseBuf, 0, blockSize, blocksToRead,
                                       MmcSectorTypes.AllTypes, false, false, true, MmcHeaderCodes.AllHeaders, true,
                                       true, MmcErrorField.None, MmcSubchannel.Raw, dev.Timeout, out _);
                    if(dev.Error || sense) blocksToRead /= 2;
                }

                if(!dev.Error || blocksToRead == 1) break;
            }

            if(dev.Error || sense)
            {
                DicConsole.WriteLine("Device error {0} trying to guess ideal transfer length.", dev.LastError);
                DicConsole.ErrorWriteLine("Device error {0} trying to guess ideal transfer length.", dev.LastError);
                return;
            }

            DicConsole.WriteLine("Reading {0} sectors at a time.", blocksToRead);

            dumpLog.WriteLine("Device reports {0} blocks ({1} bytes).", blocks, blocks * blockSize);
            dumpLog.WriteLine("Device can read {0} blocks at a time.", blocksToRead);
            dumpLog.WriteLine("Device reports {0} bytes per logical block.", blockSize);
            dumpLog.WriteLine("SCSI device type: {0}.", dev.ScsiType);
            dumpLog.WriteLine("Media identified as {0}.", dskType);
            alcohol.SetMediaType(dskType);

            dumpFile = new DataFile(outputPrefix + ".bin");
            alcohol.SetExtension(".bin");
            DataFile subFile = null;
            if(separateSubchannel) subFile = new DataFile(outputPrefix + ".sub");
            MhddLog mhddLog = new MhddLog(outputPrefix + ".mhddlog.bin", dev, blocks, blockSize, blocksToRead);
            IbgLog ibgLog = new IbgLog(outputPrefix + ".ibg", 0x0008);

            dumpFile.Seek(resume.NextBlock, (ulong)sectorSize);
            if(separateSubchannel) subFile.Seek(resume.NextBlock, subSize);

            if(resume.NextBlock > 0) dumpLog.WriteLine("Resuming from block {0}.", resume.NextBlock);

            start = DateTime.UtcNow;
            for(int t = 0; t < tracks.Length; t++)
            {
                dumpLog.WriteLine("Reading track {0}", t);

                tracks[t].BytesPerSector = sectorSize;
                tracks[t].Image = new ImageType
                {
                    format = "BINARY",
                    offset = dumpFile.Position,
                    offsetSpecified = true,
                    Value = outputPrefix + ".bin"
                };
                tracks[t].Size = (tracks[t].EndSector - tracks[t].StartSector + 1) * sectorSize;
                tracks[t].SubChannel = new SubChannelType
                {
                    Image = new ImageType {format = "rw_raw", offsetSpecified = true},
                    Size = (tracks[t].EndSector - tracks[t].StartSector + 1) * subSize
                };
                if(separateSubchannel)
                {
                    tracks[t].SubChannel.Image.offset = subFile.Position;
                    tracks[t].SubChannel.Image.Value = outputPrefix + ".sub";
                }
                else
                {
                    tracks[t].SubChannel.Image.offset = tracks[t].Image.offset;
                    tracks[t].SubChannel.Image.Value = tracks[t].Image.Value;
                }

                alcohol.SetTrackSizes((byte)(t + 1), sectorSize, tracks[t].StartSector, dumpFile.Position,
                                      tracks[t].EndSector - tracks[t].StartSector + 1);

                bool checkedDataFormat = false;

                for(ulong i = resume.NextBlock; i <= (ulong)tracks[t].EndSector; i += blocksToRead)
                {
                    if(aborted)
                    {
                        currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                        dumpLog.WriteLine("Aborted!");
                        break;
                    }

                    double cmdDuration = 0;

                    if((ulong)tracks[t].EndSector + 1 - i < blocksToRead)
                        blocksToRead = (uint)((ulong)tracks[t].EndSector + 1 - i);

#pragma warning disable RECS0018 // Comparison of floating point numbers with equality operator
                    if(currentSpeed > maxSpeed && currentSpeed != 0) maxSpeed = currentSpeed;
                    if(currentSpeed < minSpeed && currentSpeed != 0) minSpeed = currentSpeed;
#pragma warning restore RECS0018 // Comparison of floating point numbers with equality operator

                    DicConsole.Write("\rReading sector {0} of {1} at track {3} ({2:F3} MiB/sec.)", i, blocks,
                                     currentSpeed, t + 1);

                    if(readcd)
                    {
                        sense = dev.ReadCd(out readBuffer, out senseBuf, (uint)i, blockSize, blocksToRead,
                                           MmcSectorTypes.AllTypes, false, false, true, MmcHeaderCodes.AllHeaders, true,
                                           true, MmcErrorField.None, MmcSubchannel.Raw, dev.Timeout, out cmdDuration);
                        totalDuration += cmdDuration;
                    }

                    if(!sense && !dev.Error)
                    {
                        mhddLog.Write(i, cmdDuration);
                        ibgLog.Write(i, currentSpeed * 1024);
                        extents.Add(i, blocksToRead, true);
                        if(separateSubchannel)
                            for(int b = 0; b < blocksToRead; b++)
                            {
                                dumpFile.Write(readBuffer, (int)(0 + b * blockSize), sectorSize);
                                subFile.Write(readBuffer, (int)(sectorSize + b * blockSize), (int)subSize);
                            }
                        else dumpFile.Write(readBuffer);
                    }
                    else
                    {
                        // TODO: Reset device after X errors
                        if(stopOnError) return; // TODO: Return more cleanly

                        // Write empty data
                        if(separateSubchannel)
                        {
                            dumpFile.Write(new byte[sectorSize * blocksToRead]);
                            subFile.Write(new byte[subSize * blocksToRead]);
                        }
                        else dumpFile.Write(new byte[blockSize * blocksToRead]);

                        for(ulong b = i; b < i + blocksToRead; b++) resume.BadBlocks.Add(b);

                        DicConsole.DebugWriteLine("Dump-Media", "READ error:\n{0}", Sense.PrettifySense(senseBuf));
                        mhddLog.Write(i, cmdDuration < 500 ? 65535 : cmdDuration);

                        ibgLog.Write(i, 0);
                        dumpLog.WriteLine("Error reading {0} sectors from sector {1}.", blocksToRead, i);
                    }

                    if(tracks[t].TrackType1 == TrackTypeTrackType.mode1 && !checkedDataFormat)
                    {
                        byte[] sync = new byte[12];
                        Array.Copy(readBuffer, 0, sync, 0, 12);
                        if(sync.SequenceEqual(new byte[]
                        {
                            0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00
                        }))
                            switch(readBuffer[15])
                            {
                                case 0:
                                    tracks[t].TrackType1 = TrackTypeTrackType.mode0;
                                    checkedDataFormat = true;
                                    break;
                                case 1:
                                    tracks[t].TrackType1 = TrackTypeTrackType.mode1;
                                    checkedDataFormat = true;
                                    break;
                                case 2:
                                    tracks[t].TrackType1 = TrackTypeTrackType.mode2;
                                    checkedDataFormat = true;
                                    break;
                            }
                    }

                    double newSpeed = (double)blockSize * blocksToRead / 1048576 / (cmdDuration / 1000);
                    if(!double.IsInfinity(newSpeed)) currentSpeed = newSpeed;
                    resume.NextBlock = i + blocksToRead;
                }

                DiscImages.TrackType trkType;
                switch(tracks[t].TrackType1)
                {
                    case TrackTypeTrackType.audio:
                        trkType = DiscImages.TrackType.Audio;
                        break;
                    case TrackTypeTrackType.mode1:
                        trkType = DiscImages.TrackType.CdMode1;
                        break;
                    case TrackTypeTrackType.mode2:
                        trkType = DiscImages.TrackType.CdMode2Formless;
                        break;
                    case TrackTypeTrackType.m2f1:
                        trkType = DiscImages.TrackType.CdMode2Form1;
                        break;
                    case TrackTypeTrackType.m2f2:
                        trkType = DiscImages.TrackType.CdMode2Form2;
                        break;
                    case TrackTypeTrackType.dvd:
                    case TrackTypeTrackType.hddvd:
                    case TrackTypeTrackType.bluray:
                    case TrackTypeTrackType.ddcd:
                    case TrackTypeTrackType.mode0:
                        trkType = DiscImages.TrackType.Data;
                        break;
                    default: throw new ArgumentOutOfRangeException();
                }

                alcohol.SetTrackTypes((byte)(t + 1), trkType,
                                      separateSubchannel
                                          ? TrackSubchannelType.None
                                          : TrackSubchannelType.RawInterleaved);
            }

            DicConsole.WriteLine();
            end = DateTime.UtcNow;
            mhddLog.Close();
            ibgLog.Close(dev, blocks, blockSize, (end - start).TotalSeconds, currentSpeed * 1024,
                         blockSize * (double)(blocks + 1) / 1024 / (totalDuration / 1000), devicePath);
            dumpLog.WriteLine("Dump finished in {0} seconds.", (end - start).TotalSeconds);
            dumpLog.WriteLine("Average dump speed {0:F3} KiB/sec.",
                              (double)blockSize * (double)(blocks + 1) / 1024 / (totalDuration / 1000));

            #region Compact Disc Error handling
            if(resume.BadBlocks.Count > 0 && !aborted)
            {
                int pass = 0;
                bool forward = true;
                bool runningPersistent = false;

                cdRepeatRetry:
                ulong[] tmpArray = resume.BadBlocks.ToArray();
                foreach(ulong badSector in tmpArray)
                {
                    if(aborted)
                    {
                        currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                        dumpLog.WriteLine("Aborted!");
                        break;
                    }

                    DicConsole.Write("\rRetrying sector {0}, pass {1}, {3}{2}", badSector, pass + 1,
                                     forward ? "forward" : "reverse",
                                     runningPersistent ? "recovering partial data, " : "");

                    if(readcd)
                    {
                        sense = dev.ReadCd(out readBuffer, out senseBuf, (uint)badSector, blockSize, blocksToRead,
                                           MmcSectorTypes.AllTypes, false, false, true, MmcHeaderCodes.AllHeaders, true,
                                           true, MmcErrorField.None, MmcSubchannel.Raw, dev.Timeout,
                                           out double cmdDuration);
                        totalDuration += cmdDuration;
                    }

                    if((sense || dev.Error) && !runningPersistent) continue;

                    if(!sense && !dev.Error)
                    {
                        resume.BadBlocks.Remove(badSector);
                        extents.Add(badSector);
                        dumpLog.WriteLine("Correctly retried sector {0} in pass {1}.", badSector, pass);
                    }

                    if(separateSubchannel)
                    {
                        dumpFile.WriteAt(readBuffer, badSector, (uint)sectorSize, 0, sectorSize);
                        subFile.WriteAt(readBuffer, badSector, subSize, sectorSize, (int)subSize);
                    }
                    else dumpFile.WriteAt(readBuffer, badSector, blockSize);
                }

                if(pass < retryPasses && !aborted && resume.BadBlocks.Count > 0)
                {
                    pass++;
                    forward = !forward;
                    resume.BadBlocks.Sort();
                    resume.BadBlocks.Reverse();
                    goto cdRepeatRetry;
                }

                Modes.ModePage? currentModePage = null;
                byte[] md6;
                byte[] md10;

                if(!runningPersistent && persistent)
                {
                    Modes.ModePage_01_MMC pgMmc =
                        new Modes.ModePage_01_MMC {PS = false, ReadRetryCount = 255, Parameter = 0x20};
                    Modes.DecodedMode md = new Modes.DecodedMode
                    {
                        Header = new Modes.ModeHeader(),
                        Pages = new[]
                        {
                            new Modes.ModePage
                            {
                                Page = 0x01,
                                Subpage = 0x00,
                                PageResponse = Modes.EncodeModePage_01_MMC(pgMmc)
                            }
                        }
                    };
                    md6 = Modes.EncodeMode6(md, dev.ScsiType);
                    md10 = Modes.EncodeMode10(md, dev.ScsiType);

                    dumpLog.WriteLine("Sending MODE SELECT to drive.");
                    sense = dev.ModeSelect(md6, out senseBuf, true, false, dev.Timeout, out _);
                    if(sense) sense = dev.ModeSelect10(md10, out senseBuf, true, false, dev.Timeout, out _);

                    runningPersistent = true;
                    if(!sense && !dev.Error)
                    {
                        pass--;
                        goto cdRepeatRetry;
                    }
                }
                else if(runningPersistent && persistent && currentModePage.HasValue)
                {
                    Modes.DecodedMode md = new Modes.DecodedMode
                    {
                        Header = new Modes.ModeHeader(),
                        Pages = new[] {currentModePage.Value}
                    };
                    md6 = Modes.EncodeMode6(md, dev.ScsiType);
                    md10 = Modes.EncodeMode10(md, dev.ScsiType);

                    dumpLog.WriteLine("Sending MODE SELECT to drive.");
                    sense = dev.ModeSelect(md6, out senseBuf, true, false, dev.Timeout, out _);
                    if(sense) dev.ModeSelect10(md10, out senseBuf, true, false, dev.Timeout, out _);
                }

                DicConsole.WriteLine();
            }
            #endregion Compact Disc Error handling

            resume.BadBlocks.Sort();
            currentTry.Extents = ExtentsConverter.ToMetadata(extents);

            dataChk = new Checksum();
            dumpFile.Seek(0, SeekOrigin.Begin);
            if(separateSubchannel) subFile.Seek(0, SeekOrigin.Begin);
            blocksToRead = 500;

            dumpLog.WriteLine("Checksum starts.");
            for(int t = 0; t < tracks.Length; t++)
            {
                Checksum trkChk = new Checksum();
                Checksum subChk = new Checksum();

                for(ulong i = (ulong)tracks[t].StartSector; i <= (ulong)tracks[t].EndSector; i += blocksToRead)
                {
                    if(aborted)
                    {
                        dumpLog.WriteLine("Aborted!");
                        break;
                    }

                    if((ulong)tracks[t].EndSector + 1 - i < blocksToRead)
                        blocksToRead = (uint)((ulong)tracks[t].EndSector + 1 - i);

                    DicConsole.Write("\rChecksumming sector {0} of {1} at track {3} ({2:F3} MiB/sec.)", i, blocks,
                                     currentSpeed, t + 1);

                    DateTime chkStart = DateTime.UtcNow;
                    byte[] dataToCheck = new byte[blockSize * blocksToRead];
                    dumpFile.Read(dataToCheck, 0, (int)(blockSize * blocksToRead));
                    if(separateSubchannel)
                    {
                        byte[] data = new byte[sectorSize];
                        byte[] sub = new byte[subSize];
                        for(int b = 0; b < blocksToRead; b++)
                        {
                            Array.Copy(dataToCheck, 0, data, 0, sectorSize);
                            Array.Copy(dataToCheck, sectorSize, sub, 0, subSize);
                            dataChk.Update(data);
                            trkChk.Update(data);
                            subChk.Update(sub);
                        }
                    }
                    else
                    {
                        dataChk.Update(dataToCheck);
                        trkChk.Update(dataToCheck);
                    }

                    DateTime chkEnd = DateTime.UtcNow;

                    double chkDuration = (chkEnd - chkStart).TotalMilliseconds;
                    totalChkDuration += chkDuration;

                    double newSpeed = (double)blockSize * blocksToRead / 1048576 / (chkDuration / 1000);
                    if(!double.IsInfinity(newSpeed)) currentSpeed = newSpeed;
                }

                tracks[t].Checksums = trkChk.End().ToArray();
                tracks[t].SubChannel.Checksums = separateSubchannel ? subChk.End().ToArray() : tracks[t].Checksums;
            }

            DicConsole.WriteLine();
            dumpFile.Close();
            end = DateTime.UtcNow;
            dumpLog.WriteLine("Checksum finished in {0} seconds.", (end - start).TotalSeconds);
            dumpLog.WriteLine("Average checksum speed {0:F3} KiB/sec.",
                              (double)blockSize * (double)(blocks + 1) / 1024 / (totalChkDuration / 1000));

            // TODO: Correct this
            sidecar.OpticalDisc[0].Checksums = dataChk.End().ToArray();
            sidecar.OpticalDisc[0].DumpHardwareArray = resume.Tries.ToArray();
            sidecar.OpticalDisc[0].Image = new ImageType
            {
                format = "Raw disk image (sector by sector copy)",
                Value = outputPrefix + ".bin"
            };
            sidecar.OpticalDisc[0].Sessions = toc.Value.LastCompleteSession;
            sidecar.OpticalDisc[0].Tracks = new[] {tracks.Length};
            sidecar.OpticalDisc[0].Track = tracks;
            sidecar.OpticalDisc[0].Dimensions = Dimensions.DimensionsFromMediaType(dskType);
            Metadata.MediaType.MediaTypeToString(dskType, out string xmlDskTyp, out string xmlDskSubTyp);
            sidecar.OpticalDisc[0].DiscType = xmlDskTyp;
            sidecar.OpticalDisc[0].DiscSubType = xmlDskSubTyp;

            if(!aborted)
            {
                DicConsole.WriteLine("Writing metadata sidecar");

                FileStream xmlFs = new FileStream(outputPrefix + ".cicm.xml", FileMode.Create);

                XmlSerializer xmlSer = new XmlSerializer(typeof(CICMMetadataType));
                xmlSer.Serialize(xmlFs, sidecar);
                xmlFs.Close();
                alcohol.Close();
            }

            Statistics.AddMedia(dskType, true);
        }
    }
}