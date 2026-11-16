#ifndef OPC_EFM8_H
#define OPC_EFM8_H

#include <stdint.h>
#include "opc.h"

// EFM8UB1 reference manual, SFR map. USB configuration and port 2 use page 0x20.
__sfr __at (0x97) WDTCN;
__sfr __at (0xA7) SFRPAGE;
__sfr __at (0xA9) CLKSEL;
__sfr __at (0xAD) DERIVID;
__sfr __at (0xAE) USB0ADR;
__sfr __at (0xAF) USB0DAT;
__sfr __at (0xB5) USB0CF;
__sfr __at (0xB3) USB0XCN;
__sfr __at (0x8F) PSCTL;
__sfr __at (0xB7) FLKEY;
__sfr __at (0xFF) VDM0CN;
__sfr __at (0xEF) RSTSRC;
__sfr __at (0xF1) P0MDIN;
__sfr __at (0xF2) P1MDIN;
__sfr __at (0xF3) P2MDIN;
__sfr __at (0xD4) P0SKIP;
__sfr __at (0xD5) P1SKIP;
__sfr __at (0xCC) P2SKIP;
__sfr __at (0xE8) ADC0CN0;
__sfr __at (0xBC) ADC0CF;
__sfr __at (0xB3) ADC0AC;
__sfr __at (0xDF) ADC0PWR;
__sfr __at (0xB9) ADC0TK;
__sfr __at (0xBB) ADC0MX;
__sfr __at (0xBD) ADC0L;
__sfr __at (0xBE) ADC0H;
__sfr __at (0xD1) REF0CN;

extern __xdata uint8_t active_profile[OPC_PROFILE_SIZE];
extern __xdata uint8_t input_report[OPC_INPUT_SIZE];
void load_profile(void);
uint8_t save_profile(const uint8_t *candidate);
void sample_pedals(void);
void usb_init(void);
void usb_poll(void);

#endif
