#ifndef OPC_H
#define OPC_H

#include <stdint.h>

#define OPC_AXES 3
#define OPC_PROFILE_SIZE 45
#define OPC_INPUT_SIZE 13
#define OPC_AXIS_SIZE 13
#define OPC_HEADER_SIZE 6
#define OPC_OFF 255
#define OPC_MAX 4095

/* Profiles are serialized bytes, independent of compiler padding and byte order. */
void opc_defaults(uint8_t *profile);
uint8_t opc_valid(const uint8_t *profile);
/* profile must have passed opc_valid. raw contains physical channels LC1..LC3. */
void opc_input(const uint8_t *profile, const uint16_t *raw, uint8_t *report);

#endif
