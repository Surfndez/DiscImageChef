// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : Main.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Main program loop.
//
// --[ Description ] ----------------------------------------------------------
//
//     Contains the main program loop.
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
// Copyright © 2011-2019 Natalia Portillo
// ****************************************************************************/

using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using DiscImageChef.Commands;
using DiscImageChef.Console;
using DiscImageChef.Core;
using DiscImageChef.Database;
using DiscImageChef.Settings;
using Microsoft.EntityFrameworkCore;
using Mono.Options;

namespace DiscImageChef
{
    class MainClass
    {
        internal static bool                                  Verbose;
        internal static bool                                  Debug;
        internal static string                                AssemblyCopyright;
        internal static string                                AssemblyTitle;
        internal static AssemblyInformationalVersionAttribute AssemblyVersion;

        [STAThread]
        public static int Main(string[] args)
        {
            object[] attributes = typeof(MainClass).Assembly.GetCustomAttributes(typeof(AssemblyTitleAttribute), false);
            AssemblyTitle = ((AssemblyTitleAttribute)attributes[0]).Title;
            attributes    = typeof(MainClass).Assembly.GetCustomAttributes(typeof(AssemblyCopyrightAttribute), false);
            AssemblyVersion =
                Attribute.GetCustomAttribute(typeof(MainClass).Assembly, typeof(AssemblyInformationalVersionAttribute))
                    as AssemblyInformationalVersionAttribute;
            AssemblyCopyright = ((AssemblyCopyrightAttribute)attributes[0]).Copyright;

            DicConsole.WriteLineEvent      += System.Console.WriteLine;
            DicConsole.WriteEvent          += System.Console.Write;
            DicConsole.ErrorWriteLineEvent += System.Console.Error.WriteLine;

            Settings.Settings.LoadSettings();

            DicContext ctx = DicContext.Create(Settings.Settings.LocalDbPath);
            ctx.Database.Migrate();
            ctx.SaveChanges();

            bool masterDbUpdate = false;
            if(!File.Exists(Settings.Settings.MasterDbPath))
            {
                masterDbUpdate = true;
                UpdateCommand.DoUpdate(masterDbUpdate);
            }

            DicContext mctx = DicContext.Create(Settings.Settings.MasterDbPath);
            mctx.Database.Migrate();
            mctx.SaveChanges();

            if((args.Length < 1 || args[0].ToLowerInvariant() != "gui") &&
               Settings.Settings.Current.GdprCompliance < DicSettings.GdprLevel)
                new ConfigureCommand(true).Invoke(args);
            Statistics.LoadStats();
            if(Settings.Settings.Current.Stats != null && Settings.Settings.Current.Stats.ShareStats)
                Task.Run(() => { Statistics.SubmitStats(); });

            CommandSet commands = new CommandSet("DiscImageChef")
            {
                $"{AssemblyTitle} {AssemblyVersion?.InformationalVersion}",
                $"{AssemblyCopyright}",
                "",
                "usage: DiscImageChef COMMAND [OPTIONS]",
                "",
                "Global options:",
                {"verbose|v", "Shows verbose output.", b => Verbose = b        != null},
                {"debug|d", "Shows debug output from plugins.", b => Debug = b != null},
                "",
                "Available commands:",
                new AnalyzeCommand(),
                new BenchmarkCommand(),
                new ChecksumCommand(),
                new CompareCommand(),
                new ConfigureCommand(false),
                new ConvertImageCommand(),
                new CreateSidecarCommand(),
                new DecodeCommand(),
                new DeviceInfoCommand(),
                new DeviceReportCommand(),
                new DumpMediaCommand(),
                new EntropyCommand(),
                new ExtractFilesCommand(),
                new FormatsCommand(),
                new GuiCommand(),
                new ImageInfoCommand(),
                new ListDevicesCommand(),
                new ListEncodingsCommand(),
                new ListOptionsCommand(),
                new LsCommand(),
                new MediaInfoCommand(),
                new MediaScanCommand(),
                new PrintHexCommand(),
                new StatisticsCommand(),
                new UpdateCommand(masterDbUpdate),
                new VerifyCommand()
            };

            int ret = commands.Run(args);

            Statistics.SaveStats();

            return ret;
        }

        internal static void PrintCopyright()
        {
            DicConsole.WriteLine("{0} {1}", AssemblyTitle, AssemblyVersion?.InformationalVersion);
            DicConsole.WriteLine("{0}",     AssemblyCopyright);
            DicConsole.WriteLine();
        }
    }
}