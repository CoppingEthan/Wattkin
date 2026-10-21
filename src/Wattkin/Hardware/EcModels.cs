// EC register maps for MSI laptops, transcribed from the Linux msi-ec driver:
//   https://github.com/BeardOverflow/msi-ec (msi-ec.c, ec_memory_configuration.h)
//   main @ d7fbbd88e6831e56801b860e46475cbf8ddbc7c1 (2026-08-09)
// msi-ec.c is "SPDX-License-Identifier: GPL-2.0-or-later" (repo LICENSE file: GNU GPL v2 text).
// Entries follow msi-ec's CONFIGURATIONS[] order; per-firmware comments are msi-ec's own.
// MSI_EC_ADDR_UNSUPP and MSI_EC_ADDR_UNKNOWN (both 0xff01 in msi-ec) are null here.
// Only CONF_* with a charge_control_address are included.

namespace Wattkin.Hardware;

/// <summary>An EC register map for a family of MSI firmware versions. Source: msi-ec (see Source).</summary>
public sealed record EcModel(
    string Source,                    // e.g. "msi-ec CONF_G2_1"
    string[] Firmware,                // exact allowed_fw strings
    byte? ChargeAddress,              // charge_control_address; msi-ec writes 0x80 | end% (10..100), start = end - 10, and only enables it when bit7 is already set
    byte? ShiftAddress,               // shift_mode.address
    byte? ShiftEco,                   // value of the mode named "eco" (MSI "Super Battery")
    byte? ShiftBalanced,              // value of "comfort"
    byte? ShiftSport,                 // value of "sport" if present
    byte? ShiftTurbo,                 // value of "turbo"
    byte? FanModeAddress,
    byte? FanAuto, byte? FanSilent, byte? FanAdvanced,   // values of the named fan modes
    byte? CoolerBoostAddress, byte CoolerBoostBit,       // bit index 0-7
    byte? WebcamAddress, byte WebcamBit,
    byte? MicMuteLedAddress, byte? MuteLedAddress, byte LedBit,
    byte? KeyboardStateAddress, byte KeyboardStateBase, byte KeyboardMaxLevel,  // kbd_bl.bl_state_address, state_base_value, max_state (msi-ec writes base | level, reads level & 0x3)
    byte? KeyboardModeAddress, byte? KeyboardAlwaysOnValue, byte? KeyboardAutoOffValue,  // kbd_bl.bl_mode_address and bl_modes[0], bl_modes[1] (whole-byte values; the register can be shared, e.g. 0x2c with the mic-mute LED)
    byte? CpuTempAddress, byte? GpuTempAddress,          // cpu/gpu rt_temp_address
    bool VerifiedOnWindows,
    FanTableMap? FanTable = null,                        // whole fan tables written register by register (not from msi-ec)
    bool MsiSportUnclear = false);                       // msi-ec says 0xC0 is sometimes this family's turbo: it isn't taken as MSI's Sport

public static class EcModels
{
    /// <summary>
    /// Maps for exact firmwares that override msi-ec's family map. Not from msi-ec: each one names its
    /// source. Looked up before <see cref="All"/>.
    /// </summary>
    public static readonly EcModel[] Overrides =
    [
        // Stealth 15M A11UEK (MS-1563, BIOS E1563IMS.11D). msi-ec lists this firmware under CONF_G2_10,
        // which has no keyboard backlight register. The registers are those of YAMDCC's config for the
        // sibling Stealth 15M A11SDK (EC 1562EMS1.117), which YAMDCC 1.2.2 wrote on the contributor's
        // laptop: MSI's standard WMI v2 layout, keyboard 0xD3 (0x80–0x83), fan mode 0xD4 (0x0D / 0x1D /
        // 0x8D), Cooler Boost 0x98 bit 7, battery 0xD7, shift mode 0xD2 (C2 / C1 / C0 / C4), and MSI's
        // classic fan table with drop-back offsets (YAMDCC OffsetDT). Neither source has a keyboard
        // auto-off register (msi-ec: unsupported), so none here: the keys are lit over USB, and auto-off
        // is Wattkin's own (HardwareService.KeyboardLight). Verified by the contributor's hardware
        // self-test on the laptop (WMI 2.8, 2026-10-21): every write read back, Cooler Boost and
        // run-from-battery worked, and the fan target followed the table, hysteresis included.
        new EcModel(
            Source: "Stealth 15M A11UEK (YAMDCC config)",
            Firmware: ["1563EMS1.115"],
            ChargeAddress: 0xd7,
            ShiftAddress: 0xd2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: 0xc0, ShiftTurbo: 0xc4, // C0 is in YAMDCC's config ("Balanced") and passed the self-test
            FanModeAddress: 0xd4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: 0x2c, MuteLedAddress: 0x2d, LedBit: 1,
            KeyboardStateAddress: 0xd3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: null, KeyboardAutoOffValue: null,
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: true,
            FanTable: FanTableMap.MsiClassic),
    ];

    public static readonly EcModel[] All =
    [
        // CONF_G1_0 (Gen 1 / WMI1 - e.g. Prestige 14 A10SC, Prestige 15 A10SC)
        new EcModel(
            Source: "msi-ec CONF_G1_0",
            Firmware:
            [
                "14C1EMS1.012", // Prestige 14 A10SC
                "14C1EMS1.101",
                "14C1EMS1.102",
                "16S3EMS1.102", // Prestige 15 A10SC
                "16S3EMS1.103",
            ],
            ChargeAddress: 0xef,
            ShiftAddress: 0xf2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: 0xc0, ShiftTurbo: 0xc4,
            FanModeAddress: 0xf4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d, // note: msi-ec also defines fan mode "basic" = 0x4d (not mapped)
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: 0x2b, MuteLedAddress: 0x2c, LedBit: 2,
            KeyboardStateAddress: 0xf3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: 0x2c, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08,
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false),

        // CONF_G1_1 (Gen 1 / WMI1 - e.g. GP65 / GL65 Leopard, GF75 Thin, GP75 / GL75 Leopard)
        new EcModel(
            Source: "msi-ec CONF_G1_1",
            Firmware:
            [
                "16U7EMS1.105", // GP65 / GL65 Leopard 10S
                "16U7EMS1.106",
                "16U7EMS1.504", // GL65 Leopard 9SD
                "17F2EMS1.103", // GF75 Thin 9SC
                "17F2EMS1.104",
                "17F2EMS1.106",
                "17F2EMS1.107",
                "17F3EMS1.103", // GF75 Thin 9S(D/E)
                "17F3EMS1.104",
                "17F3EMS2.103", // GF75 Thin 10SER
                "17F4EMS1.101", // GF75 Thin 10SCSR
                "17F4EMS2.100", // GF75 Thin 9SCSR
                "17F5EMS1.102", // GF75 Thin 10UEK
                "17F6EMS1.101", // GF75 Thin 10UC / 10UD / 10SC
                "17F6EMS1.103",
                "17E7EMS1.103", // GP75 Leopard 10SEK
                "17E7EMS1.106", // GL75 Leopard 10SFR
                "17E7EMS1.107", // GL75 Leopard 10SDR
                "17E8EMS1.101", // GL75 Leopard 10SCXR
            ],
            ChargeAddress: 0xef,
            ShiftAddress: 0xf2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: 0xc0, ShiftTurbo: 0xc4,
            FanModeAddress: 0xf4, FanAuto: 0x0d, FanSilent: null, FanAdvanced: 0x8d, // note: no "silent" fan mode in msi-ec; msi-ec also defines "basic" = 0x4d (not mapped)
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: null, MuteLedAddress: null, LedBit: 2,
            KeyboardStateAddress: 0xf3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: 0x2c, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08,
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false),

        // CONF_G1_2 (Gen 1 / WMI1 - e.g. Alpha 15 B5EE, Alpha 17 B5EEK, Delta 15 A5EFK)
        new EcModel(
            Source: "msi-ec CONF_G1_2",
            Firmware:
            [
                "158LEMS1.103", // Alpha 15 B5EE / B5EEK
                "158LEMS1.105",
                "158LEMS1.106",
                "17LLEMS1.106", // Alpha 17 B5EEK
                "15CKEMS1.108", // Delta 15 A5EFK
            ],
            ChargeAddress: 0xef,
            ShiftAddress: 0xf2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: null, ShiftTurbo: 0xc4,
            FanModeAddress: 0xf4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: 0x2b, MuteLedAddress: 0x2c, LedBit: 2,
            KeyboardStateAddress: null, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3, // msi-ec: bl_state_address unsupported (RGB)
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08,
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false),

        // CONF_G1_3 (Gen 1 / WMI1 - e.g. GE66 Raider 10SF, GP66 Leopard 10U, GS65 / GS66 Stealth, GE76 Raider 10UG)
        new EcModel(
            Source: "msi-ec CONF_G1_3",
            Firmware:
            [
                "1541EMS1.113", // GE66 Raider 10SF
                "1542EMS1.101", // GP66 Leopard 10UG / 10UE / 10UH
                "1542EMS1.102",
                "1542EMS1.104",
                "16Q2EMS1.105", // GS65 Stealth Thin 8RE / 8RF
                "16Q2EMS1.106",
                "16Q2EMS1.107",
                "16Q2EMS1.T40",
                "16Q3EMS1.104", // P65 Creator 8RE - single color kb bl, but 00 val
                "16Q4EMS1.108", // GS65 Stealth 8S / 9S(D/F)
                "16Q4EMS1.109",
                "16Q4EMS1.110",
                "16V1EMS1.109", // GS66 Stealth 10SFS
                "16V1EMS1.111", // GS66 Stealth 10SGS
                "16V1EMS1.112",
                "16V1EMS1.116",
                "16V1EMS1.118", // GS66 Stealth 10SE
                "16V3EMS1.106", // GS66 Stealth 10UE
                "17K2EMS1.104", // GE76 Raider 10UG
            ],
            ChargeAddress: 0xef,
            ShiftAddress: 0xf2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: 0xc0, ShiftTurbo: 0xc4,
            FanModeAddress: 0xf4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: null, MuteLedAddress: null, LedBit: 2,
            KeyboardStateAddress: null, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3, // msi-ec: bl_state_address unsupported (RGB)
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08,
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false),

        // CONF_G1_4 (Gen 1 / WMI1 - e.g. GV62 8RD, Bravo 17 A4DDR / A4DDK)
        new EcModel(
            Source: "msi-ec CONF_G1_4",
            Firmware:
            [
                "16JFEMS1.105", // GV62 8RD
                "17FKEMS1.108", // Bravo 17 A4DDR / A4DDK
                "17FKEMS1.109",
                "17FKEMS1.10A",
            ],
            ChargeAddress: 0xef,
            ShiftAddress: 0xf2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: 0xc0, ShiftTurbo: 0xc4,
            FanModeAddress: 0xf4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: null, MuteLedAddress: 0x2c, LedBit: 2,
            KeyboardStateAddress: 0xf3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08, // note: bl_mode_address is MSI_EC_ADDR_UNKNOWN (unknown, not confirmed unsupported)
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false),

        // CONF_G1_5 (Gen 1 / WMI1 - e.g. Modern 14 C5M / C7M, Modern 15 A10M)
        new EcModel(
            Source: "msi-ec CONF_G1_5",
            Firmware:
            [
                "14JKEMS1.103", // Modern 14 C5M
                "14JKEMS1.104",
                "14JKEMS1.300", // Modern 14 C7M
                "14JKEMS1.501",
                "14JKEMS1.600",
                "14JKEMS1.601",
                "1551EMS1.106", // Modern 15 A10M
                "1551EMS1.107",
            ],
            ChargeAddress: 0xef,
            ShiftAddress: 0xf2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: 0xc0, ShiftTurbo: null, // no "turbo" shift mode in msi-ec
            FanModeAddress: 0xf4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: 0x2b, MuteLedAddress: 0x2c, LedBit: 2,
            KeyboardStateAddress: 0xf3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08,
            CpuTempAddress: 0x68, GpuTempAddress: null,
            VerifiedOnWindows: false),

        // CONF_G1_6 (Gen 1 / WMI1 - e.g. Modern 14 B10MW / B4MW / B5M, Modern 15 A5M / B7M)
        new EcModel(
            Source: "msi-ec CONF_G1_6",
            Firmware:
            [
                "14D1EMS1.102", // Modern 14 B10MW
                "14D1EMS1.103",
                "14DKEMS1.104", // Modern 14 B4MW
                "14DKEMS1.105",
                "14DLEMS1.105", // Modern 14 B5M
                "155LEMS1.103", // Modern 15 A5M
                "155LEMS1.104",
                "155LEMS1.105",
                "155LEMS1.106",
                "15HKEMS1.102", // Modern 15 B7M
                "15HKEMS1.104",
                "15HKEMS1.500",
                "15HKEMS1.501",
            ],
            ChargeAddress: 0xef,
            ShiftAddress: 0xf2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: 0xc0, ShiftTurbo: null, // no "turbo" shift mode in msi-ec
            FanModeAddress: 0xf4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: 0x2b, MuteLedAddress: 0x2c, LedBit: 2,
            KeyboardStateAddress: 0xf3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08, // note: bl_mode_address is MSI_EC_ADDR_UNKNOWN (unknown, not confirmed unsupported)
            CpuTempAddress: 0x68, GpuTempAddress: null,
            VerifiedOnWindows: false),

        // CONF_G1_7 (Gen 1 / WMI1 - e.g. GF63 Thin, GF65 Thin)
        new EcModel(
            Source: "msi-ec CONF_G1_7",
            Firmware:
            [
                "16R1EMS1.105", // GF63 8RC-249
                "16R3EMS1.100", // GF63 Thin 9SC
                "16R3EMS1.102",
                "16R3EMS1.104",
                "16R4EMS1.101", // GF63 Thin 10SCX(R) / 10SCS(R)
                "16R4EMS1.102",
                "16R4EMS2.101", // GF63 Thin 9SCSR
                "16R4EMS2.102",
                "16R5EMS1.101", // GF63 Thin 10U(C/D) / 10SC
                "16R5EMS1.102",
                "16W1EMS1.102", // GF65 Thin 9SE(X(R)) / 9SD
                "16W1EMS1.103",
                "16W1EMS1.104",
                "16W1EMS2.103", // GF65 Thin 10SCSXR / 10SD(R) / 10SE(R)
                "16W2EMS1.101", // GF65 Thin 10UE
            ],
            ChargeAddress: 0xef,
            ShiftAddress: 0xf2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: 0xc0, ShiftTurbo: 0xc4,
            FanModeAddress: 0xf4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d, // note: msi-ec also defines fan mode "basic" = 0x4d (not mapped)
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: null, MuteLedAddress: null, LedBit: 1,
            KeyboardStateAddress: 0xf3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08,
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false),

        // CONF_G1_8 (Gen 1 / WMI1 - e.g. Bravo 15 A4DDR, PS63 Modern 8RD)
        new EcModel(
            Source: "msi-ec CONF_G1_8",
            Firmware:
            [
                "16WKEMS1.105", // Bravo 15 A4DDR
                "16S1EMS1.104", // PS63 MODERN 8RD
                "16S1EMS1.106",
            ],
            ChargeAddress: 0xef,
            ShiftAddress: 0xf2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: null, ShiftTurbo: 0xc4,
            FanModeAddress: 0xf4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: null, MuteLedAddress: null, LedBit: 0, // note: msi-ec leaves leds.bit unset (C zero-init = 0); both LED addresses unsupported
            KeyboardStateAddress: 0xf3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08,
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false),

        // CONF_G1_9 (Gen 1 / WMI1 - e.g. GE75 Raider 10, P75 Creator 9SG, GS75 Stealth, GT76 Titan DT 9)
        new EcModel(
            Source: "msi-ec CONF_G1_9",
            Firmware:
            [
                "17E9EMS1.105", // GE75 Raider 10 SF / SFS / SGS
                "17G1EMS2.106", // P75  CREATOR 9SG
                "17G1EMS1.100", // GS75 Stealth 8SF
                "17G1EMS1.102", // GS75 Stealth 9SF
                "17G1EMS1.107",
                "17G3EMS1.110", // GS75 Stealth 10SF
                "17G3EMS1.113",
                "17G3EMS1.115",
                "17H1EMS1.103", // GT76 Titan DT 9 SF / SFS / SG / SGS
            ],
            ChargeAddress: 0xef,
            ShiftAddress: 0xf2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: 0xc0, ShiftTurbo: 0xc4,
            FanModeAddress: 0xf4, FanAuto: 0x0d, FanSilent: null, FanAdvanced: 0x8d, // note: no "silent" fan mode in msi-ec; msi-ec also defines "basic" = 0x4d (not mapped)
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: null, MuteLedAddress: null, LedBit: 1,
            KeyboardStateAddress: null, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08,
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false),

        // CONF_G1_10 omitted: charge_control_address = MSI_EC_ADDR_UNSUPP (GE63 Raider 8RE; GT72 6QE Dominator Pro; GP72 7REX Leopard Pro).

        // CONF_G1_11 (Gen 1 / WMI1 - e.g. Bravo 15 B5ED / B5DD)
        new EcModel(
            Source: "msi-ec CONF_G1_11",
            Firmware:
            [
                "158MEMS1.100", // Bravo 15 B5ED
                "158MEMS1.101",
                "158MEMS1.102",
                "158KEMS1.104", // Bravo 15 B5DD
                "158KEMS1.106",
                "158KEMS1.107",
                "158KEMS1.108",
                "158KEMS1.109",
                "158KEMS1.111",
            ],
            ChargeAddress: 0xef,
            ShiftAddress: 0xf2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: null, ShiftTurbo: 0xc4,
            FanModeAddress: 0xf4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: 0x2b, MuteLedAddress: 0x2c, LedBit: 2,
            KeyboardStateAddress: 0xf3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08, // note: bl_mode_address is MSI_EC_ADDR_UNKNOWN (unknown, not confirmed unsupported)
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false),

        // CONF_G1_13 (Gen 1 / WMI1 - e.g. Creator 15 A10SD / A10SFS / A10SET)
        new EcModel(
            Source: "msi-ec CONF_G1_13",
            Firmware:
            [
                "16V2EMS1.104", // Creator 15 A10SD
                "16V2EMS1.105", // Creator 15 A10SFS
                "16V2EMS1.106", // Creator 15 A10SET
            ],
            ChargeAddress: 0xef,
            ShiftAddress: 0xf2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: 0xc0, ShiftTurbo: null, // no "turbo" shift mode in msi-ec
            FanModeAddress: 0xf4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: 0x2b, MuteLedAddress: 0x2c, LedBit: 2,
            KeyboardStateAddress: 0xf3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08,
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false),

        // CONF_G2_0 (Gen 2 / WMI2 - e.g. Modern 14 B11M, Modern 15 A11M / B13M, Prestige A16 AI+, Summit A16 AI+)
        new EcModel(
            Source: "msi-ec CONF_G2_0",
            Firmware:
            [
                "14D2EMS1.116", // Modern 14 B11M
                "14D3EMS1.116", // Modern 14 B11MOU
                "1552EMS1.115", // Modern 15 A11M
                "1552EMS1.118",
                "1552EMS1.119",
                "1552EMS1.120",
                "159KIMS1.107", // Prestige A16 AI+ A3HMG
                "159KIMS1.108", // Summit A16 AI+ A3HMTG
                "159KIMS1.110",
                "159KIMS1.111",
                "15H1IMS1.205", // Modern 15 B12MO
                "15H1IMS1.214", // Modern 15 B13M
                "15H4IMS1.107", // Modern 15 H B13M
                "15H4IMS1.108",
                "15H4IMS1.117",
                "15H5EMS1.111", // Modern 15 H AI C1MG
            ],
            ChargeAddress: 0xd7,
            ShiftAddress: 0xd2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: null, ShiftTurbo: 0xc4,
            FanModeAddress: 0xd4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1, // msi-ec: 159K and 15H5 have no webcam control
            MicMuteLedAddress: 0x2c, MuteLedAddress: 0x2d, LedBit: 1,
            KeyboardStateAddress: 0xd3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: 0x2c, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08,
            CpuTempAddress: 0x68, GpuTempAddress: null,
            VerifiedOnWindows: false),

        // CONF_G2_1 (Gen 2 / WMI2 - e.g. Prestige 14 / 15 / 16 Studio, Katana GF66 / GF76, Pulse GL66, Cyborg 15, Stealth 15 A13V) - verified by Wattkin on Windows
        new EcModel(
            Source: "msi-ec CONF_G2_1",
            Firmware:
            [
                "14C4EMS1.120", // Prestige 14 A11SCX / A11SCS
                "14C6EMS1.109", // Prestige 14 Evo A12M
                "1581EMS1.107", // Katana GF66 11UE / 11UG
                "1582EMS1.105", // Pulse GL66 11UDK
                "1582EMS1.107", // Katana GF66 11UC / 11UD
                "1583EMS1.105", // Crosshair 15 B12UEZ / B12UGSZ
                "1583EMS1.109", // Pulse  GL66 12UEK
                "1583EMS1.110", // Pulse  GL66 12UGK / Katana GF66 12UG
                "1583EMS1.111",
                "1584EMS1.104", // Katana GF66 12U(C/D) (ENE)
                "1584EMS1.112",
                "1584IMS1.106", // Katana GF66 12UDO (ITE) (#467)
                "1585EMS1.111", // Creator M16 B13VF
                "1585EMS1.112", // Katana 15 B13VGK
                "1585EMS1.113",
                "1585EMS1.115", // Pulse 15 B13VGK
                "1585EMS2.109", // Katana 15 B12VEK / B12VFK / B12VGK
                "1585EMS2.110",
                "1585EMS2.111",
                "1585EMS2.115",
                "158NIMS1.109", // Bravo 15 C7V
                "158NIMS1.10D", // Bravo 15 C7UCX
                "158NIMS1.10E",
                "158NIMS1.30C", // Bravo 15 C7VFKP
                "158PIMS1.106", // Bravo 15 B7ED
                "158PIMS1.111",
                "158PIMS1.112",
                "158PIMS1.114",
                "158PIMS1.207", // Bravo 15 B7E
                "1591EMS1.108", // Summit E16 Flip A11UCT
                "1592EMS1.111", // Summit E16 Flip A12UCT / A12MT
                "1594EMS1.109", // Prestige 16 Studio A13VE
                "1594EMS1.113", // Wattkin addition: same laptop, newer EC; tested with CONF_G2_1 in msi-ec PR #797 (open, not in main at d7fbbd8)
                "1596EMS1.105", // Summit E16 AI Studio A1VETG
                "15H2IMS1.105", // Modern 15 B12HW
                "15K1IMS1.110", // Cyborg 15 A12VF
                "15K1IMS1.111", // Cyborg 15 A13VF
                "15K1IMS1.112", // Cyborg 15 A13VFK
                "15K1IMS1.113", // Cyborg 15 A13VF
                "16S6EMS1.111", // Prestige 15 A11SCX
                "16S6EMS1.114",
                "16S8EMS1.107", // Prestige 15 A12SC / A12UC
                "16V6EMS1.103", // Stealth 15 A13V
                "17L1EMS1.103", // Katana GF76 11UE
                "17L1EMS1.105", // Crosshair 17 A11UEK
                "17L1EMS1.106", // Katana GF76 11UG
                "17L1EMS1.107",
                "17L2EMS1.103", // Katana GF76 11UC / 11UD
                "17L2EMS1.106",
                "17L2EMS1.108", // Katana 17 B11UCX
                "17L3EMS1.106", // Crosshair 17 B12UGZ
                "17L3EMS1.109", // Katana GF76 12UG / Pulse GL76 12U
                "17L4EMS1.112", // Katana GF76 12UC
                "17LNIMS1.10E", // Bravo 17 C7VE
                "17LNIMS1.502", // Katana A17 AI B8VF
                "17LNIMS1.505",
                "17LNIMS1.506",
                "17LNIMS1.507",
                "17M1EMS2.113", // Creator 17 B11UE
            ],
            ChargeAddress: 0xd7,
            ShiftAddress: 0xd2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: null, ShiftTurbo: 0xc4,
            FanModeAddress: 0xd4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1, // Wattkin: Prestige 16 Studio A13VE (1594EMS1.113) reports no webcam switch
            MicMuteLedAddress: 0x2c, MuteLedAddress: 0x2d, LedBit: 1,
            KeyboardStateAddress: 0xd3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3, // msi-ec: "mix of single and RGB"
            KeyboardModeAddress: 0x2c, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08,
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: true),

        // CONF_G2_2 (Gen 2 / WMI2 - e.g. GP66 / GE66 / GE76, Raider GE67 / GE77, GS66 / GS76 Stealth, Alpha 17 C7V)
        new EcModel(
            Source: "msi-ec CONF_G2_2",
            Firmware:
            [
                "1543EMS1.107", // GP66 Leopard 11UH
                "1543EMS1.108", // GP66 Leopard 11UG
                "1543EMS1.113", // GE66 Raider 11UE
                "1543EMS1.115",
                "1544EMS1.107", // Vector GP66 12UGS
                "1544EMS1.112",
                "1545IMS1.109", // Raider GE67 HX 12U
                "16V4EMS1.114", // GS66 Stealth 11UE / 11UG
                "16V4EMS1.115",
                "16V4EMS1.116",
                "16V4EMS2.106", // Creator 15 A11UE
                "16V5EMS1.107", // Stealth GS66 12UE / 12UGS
                "16V5EMS1.108",
                "17K3EMS1.112", // GE76 Raider 11U / 11UH
                "17K3EMS1.113", // GE76 Raider 11UE
                "17K3EMS1.114",
                "17K3EMS1.115", // GP76 Leopard 11UG
                "17K4EMS1.108", // Raider GE76 12UE
                "17K4EMS1.112", // Raider GE76 12UGS / Vector GP76 12UH
                "17K5IMS1.107", // Raider GE77 HX 12UGS
                "17KKIMS1.108", // Alpha 17 C7VF / C7VG
                "17KKIMS1.109",
                "17KKIMS1.112",
                "17KKIMS1.114",
                "17KKIMS1.115",
                "17M1EMS1.113", // Stealth GS76 11UG
            ],
            ChargeAddress: 0xd7,
            ShiftAddress: 0xd2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: null, ShiftTurbo: 0xc4,
            FanModeAddress: 0xd4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: null, MuteLedAddress: null, LedBit: 1,
            KeyboardStateAddress: null, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: null, KeyboardAutoOffValue: null, // note: msi-ec bl_modes = { } (unspecified)
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false),

        // CONF_G2_3 (Gen 2 / WMI2 - e.g. Summit E13 / E14, Prestige 13 / 14 / 16 AI Evo, Modern 14 C12M, Venture 14 / A15)
        new EcModel(
            Source: "msi-ec CONF_G2_3",
            Firmware:
            [
                "14F1EMS1.112", // Summit E14 Flip Evo A12MT
                "14F1EMS1.114", // Summit E14 Evo A12M
                "14F1EMS1.115",
                "14F1EMS1.116",
                "14F1EMS1.117",
                "14F1EMS1.118",
                "14F1EMS1.119",
                "14F1EMS1.120",
                "14F1EMS1.207", // Prestige 14 Evo B13M
                "14F1EMS1.209", // Summit E14 Flip Evo A13MT
                "14F1EMS1.211",
                "14L1EMS1.307", // Modern 14 H D13M
                "14L1EMS1.308",
                "14L1EMS1.311",
                "14J1IMS1.109", // Modern 14 C12M
                "14J1IMS1.205",
                "14J1IMS1.209",
                "14J1IMS1.215",
                "14J1IMS1.218",
                "14N1EMS1.104", // Prestige 14 AI Evo C1MG
                "14N1EMS1.307", // Prestige 14 AI Evo C2HMG
                "13P3EMS1.107", // Summit E13 Flip A12MT
                "13P3EMS1.508", // Summit E13 Flip Evo A13MT
                "13P5EMS1.106", // Summit 13 AI+ Evo A2VM
                "13Q2EMS1.110", // Prestige 13 AI Evo A1MG
                "13Q3EMS1.111", // Prestige 13 AI+ Evo A2VMG
                "14Q2EMS1.301", // Venture 14 AI A2HMG
                "14QKIMS1.108", // Venture A14 AI+ A3HMG
                "14T2EMS1.110", // Prestige 14 Flip AI+ D3MTG
                "15A1EMS1.105", // Prestige 16 AI Evo B1MG
                "15A1EMS1.109", // Prestige 16 AI Evo B1MG (.109 EC rev)
                "15A3EMS1.104", // Prestige 16 AI+ Evo B2VMG
                "15QKIMS1.506", // Venture A15 AI A2HMG / A2HMTG
            ],
            ChargeAddress: 0xd7,
            ShiftAddress: 0xd2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: null, ShiftTurbo: 0xc4,
            FanModeAddress: 0xd4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1, // msi-ec: 13P5 has no hardware webcam control
            MicMuteLedAddress: 0x2c, MuteLedAddress: 0x2d, LedBit: 1, // msi-ec: mic-mute LED not present on 14F1; mute LED not present on 14L1, 14N1, 14QK
            KeyboardStateAddress: 0xd3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: 0x2c, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08, // msi-ec: 00 - on, 08 - 10 sec auto off
            CpuTempAddress: 0x68, GpuTempAddress: null,
            VerifiedOnWindows: false),

        // CONF_G2_4 (Gen 2 / WMI2 - e.g. Prestige 14 AI Studio C1UDXG, Cyborg 14 A13VF)
        new EcModel(
            Source: "msi-ec CONF_G2_4",
            Firmware:
            [
                "14N2EMS1.102", // Prestige 14 AI Studio C1UDXG
                "14N2EMS1.103",
                "14P1IMS1.104", // Cyborg 14 A13VF
                "14P1IMS1.106",
            ],
            ChargeAddress: 0xd7,
            ShiftAddress: 0xd2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: null, ShiftTurbo: 0xc4,
            FanModeAddress: 0xd4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: 0x2c, MuteLedAddress: 0x2d, LedBit: 1,
            KeyboardStateAddress: 0xd3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: 0x2c, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08, // msi-ec: 00 - on, 08 - 10 sec auto off
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false),

        // CONF_G2_5 (Gen 2 / WMI2 - e.g. Stealth 14 Studio A13VF, Stealth 14 AI Studio, Cyborg 15 B13WFKG)
        new EcModel(
            Source: "msi-ec CONF_G2_5",
            Firmware:
            [
                "14K1EMS1.103", // Stealth 14 Studio A13VF
                "14K1EMS1.107",
                "14K1EMS1.108",
                "14K2EMS1.104", // Stealth 14 AI Studio A1VGG / A1VFG
                "14K2EMS1.107",
                "14K2EMS1.108",
                "14K2EMS1.109",
                "15Q3EMS1.106", // Cyborg 15 B13WFKG / B2RWFKG / B2RWEKG
                "15Q3EMS1.107",
                "15Q3EMS1.108",
            ],
            ChargeAddress: 0xd7,
            ShiftAddress: 0xd2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: null, ShiftTurbo: 0xc4,
            FanModeAddress: 0xd4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: 0x2c, MuteLedAddress: 0x2d, LedBit: 1,
            KeyboardStateAddress: null, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08,
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false),

        // CONF_G2_6 (Gen 2 / WMI2 - e.g. GF63 Thin 11UC / 12V, Thin 15 B12, Thin A15 B7VF)
        new EcModel(
            Source: "msi-ec CONF_G2_6",
            Firmware:
            [
                "16R6EMS1.103", // GF63 Thin 11UC / 11SC
                "16R6EMS1.104",
                "16R6EMS1.106",
                "16R6EMS1.107",
                "16R7IMS1.005", // Thin GF63 12HW
                "16R7IMS1.104",
                "16R8IMS1.101", // Thin GF63 12VE
                "16R8IMS1.107",
                "16R8IMS1.108", // Thin GF63 12UCX
                "16R8IMS1.111", // Thin GF63 12V(E/F)
                "16R8IMS1.117", // Thin GF63 12UC
                "16R8IMS2.111", // Thin 15 B12UCX / B12VE
                "16R8IMS2.112",
                "16R8IMS2.117",
                "16RKIMS1.110", // Thin A15 B7VF
                "16RKIMS1.111",
                "16RKIMS2.108",
                "16RKIMS2.111",
            ],
            ChargeAddress: 0xd7,
            ShiftAddress: 0xd2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: null, ShiftTurbo: 0xc4,
            FanModeAddress: 0xd4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: null, MuteLedAddress: null, LedBit: 1,
            KeyboardStateAddress: 0xd3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: null, KeyboardAutoOffValue: null, // note: msi-ec bl_modes = { } (unspecified)
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false),

        // CONF_G2_10 (Gen 2 / WMI2 - e.g. Stealth 15M / 16 / 17, Raider GE68 / GE78 / 18, Vector 16 / 17, Titan GT77 / 18, Katana 15 HX)
        new EcModel(
            Source: "msi-ec CONF_G2_10",
            Firmware:
            [
                "1562EMS1.117", // Stealth 15M A11SEK
                "1563EMS1.106", // Stealth 15M A11UEK
                "1563EMS1.111",
                "1563EMS1.115",
                "1571EMS1.106", // Creator Z16 A11UE
                "1572EMS1.106", // Creator Z16 A12U
                "1572EMS1.107",
                "1587EMS1.102", // Katana 15 HX B14WEK / B14WGK
                "1587EMS1.104",
                "1587EMS1.106",
                "158NIMS1.502", // Katana A15 AI B8V
                "158NIMS1.505",
                "158NIMS1.506",
                "158NIMS1.507",
                "15B1EMS1.103", // Stealth 15M B12UE
                "15B1EMS1.105",
                "15F2EMS1.109", // Stealth 16 Studio A13VG
                "15F3EMS1.104", // Stealth 16 AI Studio A1VHG
                "15F3EMS1.105",
                "15F4EMS1.105", // Stealth 16 AI Studio A1VFG
                "15F4EMS1.106",
                "15F4EMS1.107",
                "15F5EMS1.103", // Stealth 16 AI A2HWFG
                "15FKIMS1.106", // Stealth A16 AI+ A3XVFG / A3XVGG
                "15FKIMS1.109",
                "15FKIMS1.110", // Stealth A16 AI+ A3XVGG
                "15FLIMS1.107", // Stealth A16 AI+ A3XWHG
                "15FMIBA1.102", // Stealth A16 Mercedes AMG AI+ A3XWGG
                "15G2EWS1.107", // CreatorPro Z16HXStudio B13VJTO / B13VKTO
                "15K2EMS1.106", // Cyborg 15 AI A1VFK
                "15K2EMS1.109",
                "15M1IMS1.109", // Vector GP68 HX 13V
                "15M1IMS1.110",
                "15M1IMS1.113", // Vector GP68 HX 12V
                "15M1IMS1.114",
                "15M1IMS2.104", // Raider GE68 HX 14VIG
                "15M1IMS2.105", // Vector 16 HX A13V* / A14V*
                "15M1IMS2.111",
                "15M1IMS2.112",
                "15M2IMS2.112", // Raider GE68 HX 14VGG
                "15M2IMS1.110", // Raider GE68HX 13V(F/G)
                "15M2IMS1.112", // Vector GP68HX 13VF
                "15M2IMS1.113",
                "15M2IMS1.114",
                "15M3EMS1.105", // Vector 16 HX AI A2XWHG / A2XWIG
                "15M3EMS1.106",
                "15M3EMS1.107",
                "15M3EMS1.109",
                "15M3EMS1.110",
                "15M3EMS1.112",
                "15M3EMS1.113",
                "15P2EMS1.108", // Sword 16 HX B13V / B14V
                "15P2EMS1.110",
                "15P3EMS1.103", // Pulse 16 AI C1VGKG/C1VFKG
                "15P3EMS1.106",
                "15P3EMS1.107",
                "15P4EMS1.105", // Crosshair 16 HX AI D2XW(GKG)
                "15P4EMS1.107",
                "17L5EMS1.111", // Pulse/Katana 17 B13V/GK
                "17L5EMS1.113",
                "17L5EMS1.115",
                "17L5EMS2.113", // Katana 17 B12UCXK
                "17L5EMS2.115", // Katana 17 B12VEK
                "17L7EMS1.102", // Katana 17 HX B14WGK
                "17L7EMS1.104",
                "17N1EMS1.109", // Creator Z17 A12UGST
                "17P1EMS1.104", // Stealth GS77 12U(E/GS)
                "17P1EMS1.106",
                "17P2EMS1.111", // Stealth 17 Studio A13VI
                "17Q1IMS1.10C", // Titan GT77 12UHS
                "17Q2IMS1.107", // Titan GT77HX 13VH
                "17Q2IMS1.10D",
                "17S1IMS1.105", // Raider GE78HX 13VI
                "17S1IMS1.113",
                "17S1IMS1.114",
                "17S1IMS2.104", // Raider GE78 HX 14VHG
                "17S1IMS2.107", // Vector 17 HX A14V
                "17S1IMS2.111", // Vector 17 HX A13VHG
                "17S1IMS2.112",
                "17S2IMS1.113", // Raider GE78 HX Smart Touchpad 13V
                "17S3EMS1.104", // Vector 17 HX AI A2XWHG
                "17T2EMS1.110", // Sword 17 HX B14VGKG
                "1822EMS1.105", // Titan 18 HX A14V
                "1822EMS1.109", // WMI 2.8
                "1822EMS1.111",
                "1822EMS1.112",
                "1822EMS1.114",
                "1822EMS1.115",
                "1824EMS1.107", // Titan 18 HX Dragon Edition
                "1824EMS1.108", // Raider 18 HX AI A2XWJG (MS-1824)
                "182LIMS1.108", // Vector A18 HX A9WHG
                "182LIMS1.111", // New ec version for Vector A18 HX A9WHG
                "182KIMS1.113", // Raider A18 HX A7VIG
            ],
            ChargeAddress: 0xd7,
            ShiftAddress: 0xd2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: null, ShiftTurbo: 0xc4, // note: msi-ec says turbo is "sometimes 0xc0"
            FanModeAddress: 0xd4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
            CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
            WebcamAddress: 0x2e, WebcamBit: 1,
            MicMuteLedAddress: 0x2c, MuteLedAddress: 0x2d, LedBit: 1,
            KeyboardStateAddress: null, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
            KeyboardModeAddress: null, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08,
            CpuTempAddress: 0x68, GpuTempAddress: 0x80,
            VerifiedOnWindows: false,
            MsiSportUnclear: true), // not msi-ec's: see the turbo note above
    ];

    /// <summary>
    /// MSI's standard layout on laptops with the WMI v2 firmware interface (roughly 2020 onwards):
    /// the addresses MSI Center itself hard-codes for every such notebook (battery 0xD7, shift mode
    /// 0xD2 with C4/C1/C2, fan mode 0xD4, Cooler Boost 0x98, keyboard 0xD3/0x2C, mute LEDs 0x2C/0x2D).
    /// Used when a firmware isn't in the msi-ec table, and only after the registers pass a sanity check.
    /// </summary>
    public static readonly EcModel MsiStandardWmi2 = new(
        Source: "MSI standard layout (WMI v2)",
        Firmware: [],
        ChargeAddress: 0xd7,
        ShiftAddress: 0xd2, ShiftEco: 0xc2, ShiftBalanced: 0xc1, ShiftSport: null, ShiftTurbo: 0xc4,
        FanModeAddress: 0xd4, FanAuto: 0x0d, FanSilent: 0x1d, FanAdvanced: 0x8d,
        CoolerBoostAddress: 0x98, CoolerBoostBit: 7,
        WebcamAddress: 0x2e, WebcamBit: 1,
        MicMuteLedAddress: 0x2c, MuteLedAddress: 0x2d, LedBit: 1,
        KeyboardStateAddress: 0xd3, KeyboardStateBase: 0x80, KeyboardMaxLevel: 3,
        KeyboardModeAddress: 0x2c, KeyboardAlwaysOnValue: 0x00, KeyboardAutoOffValue: 0x08,
        CpuTempAddress: 0x68, GpuTempAddress: 0x80,
        VerifiedOnWindows: false);

    /// <summary>Exact firmware match (case-insensitive), an override first, else null.</summary>
    public static EcModel? Find(string firmware) =>
        Array.Find(Overrides, m => Matches(m, firmware)) ?? Array.Find(All, m => Matches(m, firmware));

    private static bool Matches(EcModel model, string firmware) =>
        Array.Exists(model.Firmware, fw => string.Equals(fw, firmware, StringComparison.OrdinalIgnoreCase));
}
