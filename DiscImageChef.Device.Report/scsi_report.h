//
// Created by claunia on 14/12/17.
//

#ifndef DISCIMAGECHEF_DEVICE_REPORT_SCSI_REPORT_H
#define DISCIMAGECHEF_DEVICE_REPORT_SCSI_REPORT_H
#define DIC_SCSI_REPORT_ELEMENT "SCSI"
#define DIC_SCSI_INQUIRY_ELEMENT "Inquiry"

void ScsiReport(int fd, xmlTextWriterPtr xmlWriter);

#endif //DISCIMAGECHEF_DEVICE_REPORT_SCSI_REPORT_H