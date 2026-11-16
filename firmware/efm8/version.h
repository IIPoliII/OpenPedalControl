#ifndef OPC_VERSION_H
#define OPC_VERSION_H

/* One version source for the USB descriptor and upgrade manifest. */
#define OPC_VERSION_MAJOR 1
#define OPC_VERSION_MINOR 13
#define OPC_VERSION_PATCH 0
#define OPC_SYSCLK_HZ 24500000
#define OPC_ADC_BITS 12
#define OPC_USB_INTERVAL_MS 4
#define OPC_BCD(v) (((v) / 10) * 16 + (v) % 10)

#if OPC_SYSCLK_HZ != 24500000 || OPC_ADC_BITS != 12 || OPC_USB_INTERVAL_MS != 4
#error Changing clock, ADC mode or report interval requires corresponding hardware implementation and validation.
#endif

#endif
