#include "efm8.h"

// HE SIM PEDALS uses P0.0/VREF as the external ADC reference.
static const __code uint8_t adc_mux[3] = { OPC_LC1, OPC_LC2, OPC_LC3 };
static __xdata uint16_t raw[3];
__xdata uint8_t input_report[OPC_INPUT_SIZE];

unsigned char __sdcc_external_startup(void)
{
    WDTCN = 0xDE;
    WDTCN = 0xAD;
    return 0;
}

static void adc_init(void)
{
    uint8_t i, channel, mask;
    SFRPAGE = 0x20;
    P0MDIN &= ~1u;   // External VREF input, confirmed in the original firmware.
    P0SKIP |= 1u;
    for (i = 0; i < 3; i++) {
        channel = adc_mux[i];
        if (channel < 8) {
            mask = 1u << channel;
            P0MDIN &= ~mask;
            P0SKIP |= mask;
        } else if (channel < 16) {
            mask = 1u << (channel - 8);
            P1MDIN &= ~mask;
            P1SKIP |= mask;
        } else {
            mask = 1u << (channel - 20);  // ADC0.20 .. 23 correspond to P2.0 .. 3.
            P2MDIN &= ~mask;
            P2SKIP |= mask;
        }
    }
    SFRPAGE = 0;
    REF0CN = 0x00;   // External P0.0/VREF, GND pin. Original init at 0x1ED4.
    ADC0CF = 0x13;   // HFOSC0 / 3, delayed tracking, gain 1 for LC1..3 (0x1AC0).
    ADC0AC = 0x82;   // Accumulate two 12-bit results, as in the original firmware.
    ADC0PWR = 0x4F;
    ADC0TK = 0xBF;
    ADC0CN0 = 0xC0;  // Always-on burst mode with software trigger.
}

void sample_pedals(void)
{
    uint8_t i;
    for (i = 0; i < 3; i++) {
        ADC0MX = adc_mux[i];
        ADC0CN0 = 0xD0;  // Clear ADINT and start a conversion.
        while (!(ADC0CN0 & 0x20)) {}
        raw[i] = (((uint16_t)ADC0H << 8) | ADC0L) >> 1;  // Average, range 0..4092.
    }
    opc_input(active_profile, raw, input_report);
}

void main(void)
{
    SFRPAGE = 0;
    if (DERIVID != 0x41) {  // EFM8UB10F16G-QFN28
        for (;;) {}
    }
    CLKSEL = 0;  // HFOSC0, 24.5 MHz.
    while (!(CLKSEL & 0x80)) {}
    VDM0CN = 0x80;
    while (!(VDM0CN & 0x40)) {}
    RSTSRC = 0x02;  // Supply monitor is required for flash updates.
    adc_init();
    load_profile();
    sample_pedals();
    usb_init();
    for (;;) usb_poll();
}
