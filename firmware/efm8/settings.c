#include "efm8.h"
#include <string.h>

// Two 64-byte pages in the device's separate nonvolatile data area.
#define SLOT_A 0xF800u
#define SLOT_B 0xF840u
#define CRC_OFFSET (1 + OPC_PROFILE_SIZE)
#define COMMIT_OFFSET (CRC_OFFSET + 2)
#define COMMITTED 0xA5

__xdata uint8_t active_profile[OPC_PROFILE_SIZE];
static uint16_t current_slot;

static uint16_t crc16(const uint8_t *bytes, uint8_t length)
{
    uint16_t crc = 0xFFFF;
    uint8_t bit;
    while (length--) {
        crc ^= ((uint16_t)*bytes++) << 8;
        for (bit = 0; bit < 8; bit++) {
            if (crc & 0x8000) crc = (crc << 1) ^ 0x1021;
            else crc <<= 1;
        }
    }
    return crc;
}

static uint8_t valid_slot(const __code uint8_t *slot)
{
    uint16_t stored_crc;
    if (slot[COMMIT_OFFSET] != COMMITTED) return 0;
    stored_crc = slot[CRC_OFFSET] | ((uint16_t)slot[CRC_OFFSET + 1] << 8);
    return crc16(slot, CRC_OFFSET) == stored_crc && opc_valid(slot + 1);
}

void load_profile(void)
{
    const __code uint8_t *a = (const __code uint8_t *)SLOT_A;
    const __code uint8_t *b = (const __code uint8_t *)SLOT_B;
    uint8_t a_ok = valid_slot(a), b_ok = valid_slot(b);
    current_slot = 0;
    if (b_ok && (!a_ok || (uint8_t)(b[0] - a[0]) == 1)) current_slot = SLOT_B;
    else if (a_ok) current_slot = SLOT_A;
    if (current_slot) memcpy(active_profile, (const __code uint8_t *)(current_slot + 1), OPC_PROFILE_SIZE);
    else opc_defaults(active_profile);
}

// The CPU stalls on the flash MOVX. No interrupts are enabled in this polled firmware.
// SDCC's small model keeps the function arguments and temporaries out of XRAM.
static void flash_op(uint16_t address, uint8_t value, uint8_t mode)
{
    if (address < SLOT_A || address >= SLOT_B + 64) return;
    FLKEY = 0xA5;
    FLKEY = 0xF1;
    PSCTL = mode;
    *(volatile __xdata uint8_t *)address = value;
    PSCTL = 0;
}

uint8_t save_profile(const uint8_t *candidate)
{
    uint16_t target, crc;
    uint8_t i, generation = 0;
    const __code uint8_t *slot;
    if (!opc_valid(candidate)) return 0;
    if (!memcmp(active_profile, candidate, OPC_PROFILE_SIZE)) return 1;
    target = current_slot == SLOT_A ? SLOT_B : SLOT_A;
    slot = (const __code uint8_t *)target;
    if (current_slot) generation = *((const __code uint8_t *)current_slot) + 1;
    flash_op(target, 0, 3);  // Erase only the inactive slot.
    if (slot[COMMIT_OFFSET] != 0xFF) return 0;
    flash_op(target, generation, 1);
    for (i = 0; i < OPC_PROFILE_SIZE; i++) flash_op(target + 1 + i, candidate[i], 1);
    if (slot[0] != generation || memcmp(slot + 1, candidate, OPC_PROFILE_SIZE)) return 0;
    crc = crc16(slot, CRC_OFFSET);
    flash_op(target + CRC_OFFSET, (uint8_t)crc, 1);
    flash_op(target + CRC_OFFSET + 1, crc >> 8, 1);
    flash_op(target + COMMIT_OFFSET, COMMITTED, 1);
    if (!valid_slot(slot)) return 0;
    current_slot = target;
    memcpy(active_profile, candidate, OPC_PROFILE_SIZE);
    return 1;
}
