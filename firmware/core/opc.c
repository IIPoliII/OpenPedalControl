#include "opc.h"

static uint16_t read_u16(const uint8_t *p)
{
    return (uint16_t)(p[0] | ((uint16_t)p[1] << 8));
}

static void write_u16(uint8_t *p, uint16_t value)
{
    p[0] = (uint8_t)value;
    p[1] = (uint8_t)(value >> 8);
}

void opc_defaults(uint8_t *profile)
{
    uint8_t i, point;
    uint8_t *axis = profile + OPC_HEADER_SIZE;
    profile[0] = 2;
    profile[1] = 'O';
    profile[2] = 'P';
    profile[3] = 'C';
    profile[4] = 1;
    profile[5] = OPC_AXES;
    for (i = 0; i < OPC_AXES; ++i, axis += OPC_AXIS_SIZE) {
        axis[0] = i;
        write_u16(axis + 1, 0);
        write_u16(axis + 3, 4092); /* UB1 native 12-bit range, after averaging. */
        axis[5] = axis[6] = 0;
        for (point = 0; point < 6; ++point)
            axis[7 + point] = point * 20;
    }
}

uint8_t opc_valid(const uint8_t *profile)
{
    uint8_t used = 0, i, point;
    const uint8_t *axis = profile + OPC_HEADER_SIZE;
    if (profile[0] != 2 || profile[1] != 'O' || profile[2] != 'P' ||
        profile[3] != 'C' || profile[4] != 1 || profile[5] != OPC_AXES)
        return 0;
    for (i = 0; i < OPC_AXES; ++i, axis += OPC_AXIS_SIZE) {
        uint16_t rest = read_u16(axis + 1);
        uint16_t full = read_u16(axis + 3);
        if (axis[0] != OPC_OFF) {
            if (axis[0] >= OPC_AXES || (used & (1u << axis[0])) || rest == full)
                return 0;
            used |= (uint8_t)(1u << axis[0]);
        }
        if (rest > OPC_MAX || full > OPC_MAX || axis[5] > 90 || axis[6] > 90 ||
            axis[5] + axis[6] >= 100)
            return 0;
        for (point = 0; point < 6; ++point)
            if (axis[7 + point] > 100)
                return 0;
    }
    return 1;
}

static uint16_t apply_axis(const uint8_t *axis, uint16_t raw)
{
    int16_t rest = (int16_t)read_u16(axis + 1);
    int16_t distance = (int16_t)read_u16(axis + 3) - rest;
    int16_t offset = (int16_t)raw - rest;
    int32_t position, span;
    uint16_t fraction;
    uint8_t segment;
    const uint8_t *points = axis + 7;
    if (distance < 0) {
        distance = -distance;
        offset = -offset;
    }
    position = (int32_t)offset * 100 - (int32_t)distance * axis[5];
    span = (int32_t)distance * (100 - axis[5] - axis[6]);
    if (position <= 0)
        return (uint16_t)(((uint32_t)points[0] * OPC_MAX + 50) / 100);
    if (position >= span || span < 100)
        return (uint16_t)(((uint32_t)points[5] * OPC_MAX + 50) / 100);

    /* Locate the segment before rounding. Even a 0-to-100 step within a single segment
       retains the full resolution of the output. The largest product fits signed 32 bits. */
    position *= 5;
    segment = (uint8_t)(position / span);
    position -= segment * span;
    fraction = (uint16_t)((position * OPC_MAX + span / 2) / span);
    return (uint16_t)(((uint32_t)points[segment] * (OPC_MAX - fraction) +
                       (uint32_t)points[segment + 1] * fraction + 50) / 100);
}

void opc_input(const uint8_t *profile, const uint16_t *raw, uint8_t *report)
{
    uint8_t i;
    const uint8_t *axis = profile + OPC_HEADER_SIZE;
    report[0] = 1;
    for (i = 0; i < OPC_AXES; ++i, axis += OPC_AXIS_SIZE) {
        uint16_t cooked = axis[0] == OPC_OFF ? 0 : apply_axis(axis, raw[axis[0]]);
        write_u16(report + 1 + 2 * i, cooked);
        write_u16(report + 7 + 2 * i, raw[i]);
    }
}
