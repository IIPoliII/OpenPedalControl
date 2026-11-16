#include "efm8.h"
#include <string.h>

#ifdef OPC_DIAGNOSTIC
#define GAME_INPUT_SIZE 16
#else
#define GAME_INPUT_SIZE 8
#endif
#define FEATURE_PAYLOAD_SIZE 56

// EFM8UB1 USB0 indirect and endpoint-indexed registers.
enum {
    FADDR = 0x00, POWER = 0x01, IN1INT = 0x02, CMINT = 0x06,
    FRAME_LOW = 0x0C, INDEX = 0x0E, CLKREC = 0x0F,
    EP_CSR = 0x11, IN_CSR_HIGH = 0x12, EP0_COUNT = 0x16,
    ENABLE = 0x1E, FIFO0 = 0x20, FIFO1 = 0x21
};
enum {
    OUT_READY = 1, IN_READY = 2, SENT_STALL = 4, DATA_END = 8, SETUP_END = 16,
    SEND_STALL = 32, SERVICE_OUT = 64, SERVICE_SETUP_END = 128
};

static const __code uint8_t report_descriptor[] = {
#ifdef OPC_DIAGNOSTIC
    0x05,0x01,0x09,0x05,0xA1,0x01,0x09,0x01,0xA1,0x00,
    0x05,0x09,0x19,0x01,0x29,0x0C,0x15,0x00,0x25,0x01,
    0x75,0x01,0x95,0x0C,0x81,0x02,0x95,0x01,0x75,0x04,0x81,0x03,
    0x05,0x01,0x09,0x30,0x09,0x31,0x09,0x32,0x09,0x33,
    0x09,0x34,0x09,0x35,0x09,0x36,0x15,0x00,0x26,0xFF,0x0F,
    0x95,0x07,0x75,0x10,0x81,0x02,0xC0,0xC0
#else
    0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, // Original Game Pad top-level usage
    0x09, 0x01, 0xA1, 0x00,              // Original Pointer physical collection
    0x05, 9, 0x19, 1, 0x29, 12, 0x25, 1,
    0x75, 1, 0x95, 12, 0x81, 2,          // Twelve reserved button bits
    0x95, 1, 0x75, 4, 0x81, 3,           // Four padding bits
    0x05, 1, 0x09, 0x30, 0x09, 0x31, 0x09, 0x32, 0x26, 0xFF, 0x0F,
    0x95, 3, 0x75, 16, 0x81, 2, 0xC0,   // Declare every game axis individually
    0x06, 0x00, 0xFF, 0x09, 1, 0x25, 0xFF,
    0x75, 8, 0x95, FEATURE_PAYLOAD_SIZE, 0xB1, 0x02, 0xC0 // profile + cooked/raw snapshot
#endif
};
typedef char report_descriptor_fits_control_packet[(sizeof(report_descriptor) < 64) ? 1 : -1];
static const __code uint8_t device_descriptor[] = {
    18, 1, 0x00, 0x02, 0, 0, 0, 64,
    OPC_USB_VID & 255, OPC_USB_VID >> 8, OPC_USB_PID & 255, OPC_USB_PID >> 8,
    OPC_BCD(OPC_VERSION_MINOR), OPC_BCD(OPC_VERSION_MAJOR), 2, 1, 3, 1
};
static const __code uint8_t configuration_descriptor[] = {
    9, 2, 34, 0, 1, 1, 0, 0x80, 50,         // bus powered, 100 mA maximum
    9, 4, 0, 0, 1, 3, 0, 0, 0,              // HID with no boot protocol
    9, 0x21, 0x11, 0x01, 0, 1, 0x22, sizeof(report_descriptor), 0,
    7, 5, 0x81, 3, 64, 0, OPC_USB_INTERVAL_MS
};
static const __code uint8_t languages[] = {4, 3, 9, 4};
static const __code uint8_t serial[] = {
    26, 3, 'O',0,'P',0,'C',0,'-',0,'T',0,'E',0,'S',0,'T',0,'0',0,'0',0,'0',0,'1',0
};
static const __code uint8_t manufacturer[] = {
    34, 3, 'O',0,'p',0,'e',0,'n',0,'P',0,'e',0,'d',0,'a',0,'l',0,
    'C',0,'o',0,'n',0,'t',0,'r',0,'o',0,'l',0
};
static const __code uint8_t product[] = {
    48, 3, 'O',0,'p',0,'e',0,'n',0,'P',0,'e',0,'d',0,'a',0,'l',0,
    'C',0,'o',0,'n',0,'t',0,'r',0,'o',0,'l',0,' ',0,'P',0,'e',0,'d',0,'a',0,'l',0,'s',0
};

static __xdata uint8_t setup[8], incoming[OPC_PROFILE_SIZE], last_sent[GAME_INPUT_SIZE];
static __xdata uint8_t raw_snapshot[OPC_INPUT_SIZE];
static __xdata uint8_t feature_report[OPC_PROFILE_SIZE + OPC_INPUT_SIZE - 2];
static __xdata uint8_t game_report[GAME_INPUT_SIZE];
static uint8_t configured, halted, awaiting_profile, idle_rate, elapsed_idle;
static uint8_t have_report, last_frame;
static uint8_t boot_requested, boot_request_frame;
#ifdef OPC_DIAGNOSTIC
static uint16_t diagnostic_ms;
static uint8_t diagnostic_last_frame;
#endif

static uint8_t reg_read(uint8_t reg)
{
    USB0ADR = reg | 0x80;
    while (USB0ADR & 0x80) {}
    return USB0DAT;
}

static void reg_write(uint8_t reg, uint8_t value)
{
    USB0ADR = reg;
    USB0DAT = value;
}

static void read_fifo(uint8_t *dst, uint8_t count)
{
    while (count--) *dst++ = reg_read(FIFO0);
}

static void write_fifo(uint8_t fifo, const uint8_t *src, uint8_t count)
{
    while (count--) reg_write(fifo, *src++);
}

// Every control response fits a single 64-byte packet, including strings.
static void reply(const uint8_t *src, uint8_t length)
{
    if (setup[7] == 0 && length > setup[6]) length = setup[6];
    reg_write(EP_CSR, SERVICE_OUT);
    write_fifo(FIFO0, src, length);
    reg_write(EP_CSR, IN_READY | DATA_END);
}

static void acknowledge(void)
{
    reg_write(EP_CSR, SERVICE_OUT | DATA_END);
}

static void stall(void)
{
    awaiting_profile = 0;
    reg_write(EP_CSR, SERVICE_OUT | SEND_STALL);
}

static void endpoint_configuration(uint8_t enable)
{
    configured = enable;
    have_report = halted = elapsed_idle = 0;
    reg_write(ENABLE, enable ? 0x13 : 0x11);
    reg_write(INDEX, 1);
    reg_write(IN_CSR_HIGH, 0x20);  // unsplit FIFO, IN direction
    reg_write(EP_CSR, 0x48);       // clear data toggle and discard any queued report
    reg_write(INDEX, 0);
}

static void descriptor(uint8_t type, uint8_t index)
{
    if (setup[0] == 0x80 && !setup[4] && !setup[5] && !index) {
        if (type == 1) { reply(device_descriptor, sizeof(device_descriptor)); return; }
        if (type == 2) { reply(configuration_descriptor, sizeof(configuration_descriptor)); return; }
        if (type == 3) { reply(languages, sizeof(languages)); return; }
    }
    if (setup[0] == 0x80 && type == 3 && index == 1 && setup[4] == 9 && setup[5] == 4) {
        reply(product, sizeof(product)); return;
    }
    if (setup[0] == 0x80 && type == 3 && index == 2 && setup[4] == 9 && setup[5] == 4) {
        reply(manufacturer, sizeof(manufacturer)); return;
    }
    if (setup[0] == 0x80 && type == 3 && index == 3 && setup[4] == 9 && setup[5] == 4) {
        reply(serial, sizeof(serial)); return;
    }
    if (setup[0] == 0x81 && !setup[4] && !setup[5] && !index) {
        if (type == 0x21) { reply(configuration_descriptor + 18, 9); return; }
        if (type == 0x22) { reply(report_descriptor, sizeof(report_descriptor)); return; }
    }
    stall();
}

static void handle_setup(void)
{
    uint8_t type = setup[0], request = setup[1];
    uint16_t value = setup[2] | ((uint16_t)setup[3] << 8);
    uint16_t index = setup[4] | ((uint16_t)setup[5] << 8);
    uint16_t length = setup[6] | ((uint16_t)setup[7] << 8);
    uint8_t status[2] = {0, 0};
    if ((type == 0x80 || type == 0x81) && request == 6) {
        descriptor(setup[3], setup[2]); return;
    }
    if (!type && request == 5 && value < 128 && !index && !length && !configured) {
        reg_write(FADDR, (uint8_t)value);  // USB0 applies the address after the status stage.
        acknowledge(); return;
    }
    if (type == 0x80 && request == 8 && !value && !index && length == 1) {
        reply(&configured, 1); return;
    }
    if (!type && request == 9 && value <= 1 && !index && !length) {
        endpoint_configuration((uint8_t)value);
        acknowledge(); return;
    }
    if (request == 0 && !value && length == 2) {
        if ((type == 0x80 && !index) || (type == 0x81 && !index && configured) ||
            (type == 0x82 && (index == 0 || index == 0x80 || (index == 0x81 && configured)))) {
            if (type == 0x82 && index == 0x81) status[0] = halted;
            reply(status, 2); return;
        }
    }
    if (!configured) { stall(); return; }
    if (type == 2 && (request == 1 || request == 3) && !value && index == 0x81 && !length) {
        halted = request == 3;
        reg_write(INDEX, 1);
        reg_write(EP_CSR, halted ? 0x10 : 0x48);
        reg_write(INDEX, 0);
        have_report = 0;
        acknowledge(); return;
    }
    if (index) { stall(); return; }
    if (type == 0x81 && request == 10 && !value && length == 1) { reply(status, 1); return; }
    if (type == 1 && request == 11 && !value && !length) {
        endpoint_configuration(1); acknowledge(); return;
    }
    if (type == 0xA1 && request == 1) {
        if (value == 0x0300) {
            sample_pedals();
            memcpy(feature_report, active_profile + 1, OPC_PROFILE_SIZE - 1);
            memcpy(feature_report + OPC_PROFILE_SIZE - 1, input_report + 1, OPC_INPUT_SIZE - 1);
            reply(feature_report, FEATURE_PAYLOAD_SIZE); return;
        }
        if (value == 0x0302) { reply(active_profile, OPC_PROFILE_SIZE); return; }
        if (value == 0x0100 || value == 0x0101) {
            sample_pedals();
            memset(game_report, 0, GAME_INPUT_SIZE);
            memcpy(game_report + 2, input_report + 1, 6);
            reply(game_report, GAME_INPUT_SIZE); return;
        }
        if (value == 0x0303) {
            sample_pedals();
            memcpy(raw_snapshot, input_report, OPC_INPUT_SIZE);
            raw_snapshot[0] = 3;
            reply(raw_snapshot, OPC_INPUT_SIZE); return;
        }
    }
    if (type == 0x21 && request == 9 &&
        ((value == 0x0302 && length == OPC_PROFILE_SIZE) || (value == 0x0300 && (length == 6 || length == 56)))) {
        awaiting_profile = value == 0x0300 ? (length == 6 ? 3 : 2) : 1;
        reg_write(EP_CSR, SERVICE_OUT); return;
    }
    if (type == 0xA1 && request == 2 && value <= 1 && length == 1) { reply(&idle_rate, 1); return; }
    if (type == 0x21 && request == 10 && setup[2] <= 1 && !length) {
        idle_rate = setup[3]; elapsed_idle = 0;
        acknowledge(); return;
    }
    stall();
}

static void service_control(void)
{
    uint8_t csr, count;
    reg_write(INDEX, 0);
    csr = reg_read(EP_CSR);
    if (csr & SENT_STALL) {
        awaiting_profile = 0;
        reg_write(EP_CSR, 0);
    }
    if (csr & SETUP_END) {
        awaiting_profile = 0;
        reg_write(EP_CSR, SERVICE_SETUP_END);
    }
    if (!(csr & OUT_READY)) return;
    count = reg_read(EP0_COUNT);
    if (awaiting_profile) {
        uint8_t mode = awaiting_profile;
        uint8_t unnumbered = mode == 2;
        awaiting_profile = 0;
        if (count != (mode == 3 ? 6 : unnumbered ? 56 : OPC_PROFILE_SIZE)) { stall(); return; }
        if (mode == 3) {
            incoming[0] = 2;
            read_fifo(incoming + 1, 6);
        } else if (unnumbered) {
            incoming[0] = 2;
            read_fifo(incoming + 1, OPC_PROFILE_SIZE - 1);
            for (count = 0; count < 12; count++) reg_read(FIFO0);
        } else read_fifo(incoming, count);
        if (incoming[0] == 2 && incoming[1] == 'B' && incoming[2] == 'O' &&
            incoming[3] == 'O' && incoming[4] == 'T' && incoming[5] == 1) {
            // Explicit maintenance command. A normal OPC profile cannot match it.
            boot_request_frame = reg_read(FRAME_LOW);
            boot_requested = 1;
            acknowledge();
            return;
        }
        if (mode == 3) { stall(); return; }
        if (save_profile(incoming)) acknowledge();
        else stall();
    } else if (count == sizeof(setup)) {
        read_fifo(setup, sizeof(setup));
        handle_setup();
    } else {
        stall();
    }
}

void usb_init(void)
{
    SFRPAGE = 0x20;
    USB0CF = 0;       // Bus powered. USB clock is the internal 48 MHz HFOSC1.
    reg_write(POWER, 0x08);  // Reset USB0, including any state inherited from a bootloader.
    USB0XCN = 0x60;   // Full-speed PHY, detached.
    reg_write(CLKREC, 0x8F);
    reg_write(POWER, 1);
    USB0XCN = 0xE0;   // Attach D+ pull-up after initialization.
    SFRPAGE = 0;
}

void usb_poll(void)
{
    uint8_t frame;
#ifdef OPC_DIAGNOSTIC
    frame = reg_read(FRAME_LOW);
    if (configured) {
        diagnostic_ms += (uint8_t)(frame - diagnostic_last_frame);
        if (diagnostic_ms >= 10000) {
            boot_requested = 1;
            boot_request_frame = frame - 100;
        }
    }
    diagnostic_last_frame = frame;
#endif
    if (boot_requested && (uint8_t)(reg_read(FRAME_LOW) - boot_request_frame) >= 100) {
        // Original boot entry at FBC0 accepts software reset with R0=A5.
        // Delay until the SET_REPORT status stage has completed.
        __asm
            mov r0,#0xa5
            mov _RSTSRC,#0x10
        __endasm;
    }
    if (reg_read(CMINT) & 4) {
        awaiting_profile = idle_rate = 0;
        endpoint_configuration(0);
    }
    if (reg_read(IN1INT) & 1) service_control();
    if (!configured || halted) return;
    frame = reg_read(FRAME_LOW);
    if ((uint8_t)(frame - last_frame) < OPC_USB_INTERVAL_MS) return;
    last_frame = frame;
    if (elapsed_idle != 255) elapsed_idle++;
    reg_write(INDEX, 1);
    if (reg_read(EP_CSR) & 1) return;
    sample_pedals();
    memset(game_report, 0, GAME_INPUT_SIZE);
    memcpy(game_report + 2, input_report + 1, 6);
    if (have_report && (!idle_rate || elapsed_idle < idle_rate) &&
        !memcmp(game_report, last_sent, GAME_INPUT_SIZE)) return;
    write_fifo(FIFO1, game_report, GAME_INPUT_SIZE);
    reg_write(EP_CSR, 1);
    memcpy(last_sent, game_report, GAME_INPUT_SIZE);
    elapsed_idle = 0;
    have_report = 1;
}
