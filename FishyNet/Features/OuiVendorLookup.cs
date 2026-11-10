using System.Collections.Frozen;
using FishyNet.Core.Packets;

namespace FishyNet.Features;

public readonly record struct VendorInfo(string VendorName, string DefaultCategory);

public static class OuiVendorLookup
{
    private static readonly FrozenDictionary<uint, VendorInfo> Table = CreateTable();

    public static VendorInfo Resolve(MacAddress mac)
    {
        if (mac.IsZero) return new VendorInfo("Unknown", "Device");
        if (mac.IsBroadcast) return new VendorInfo("Broadcast", "Network");

        uint oui = mac.OuiPrefix;
        if (Table.TryGetValue(oui, out VendorInfo info))
        {
            return info;
        }

        // Check locally administered bit (bit 1 of first octet: x2:xx:xx, x6:xx:xx, xA:xx:xx, xE:xx:xx)
        byte firstOctet = (byte)((oui >> 16) & 0xFF);
        if ((firstOctet & 0x02) != 0)
        {
            return new VendorInfo("Private / Randomized MAC", "Mobile");
        }

        return new VendorInfo("Unknown Vendor", "Device");
    }

    private static FrozenDictionary<uint, VendorInfo> CreateTable()
    {
        var dict = new Dictionary<uint, VendorInfo>
        {
            // Virtualization & Cloud
            [0x000569] = new("VMware", "Virtual"),
            [0x000C29] = new("VMware", "Virtual"),
            [0x005056] = new("VMware", "Virtual"),
            [0x00155D] = new("Microsoft Hyper-V", "Virtual"),
            [0x080027] = new("Oracle VirtualBox", "Virtual"),
            [0x525400] = new("QEMU / KVM", "Virtual"),

            // Routers & Networking
            [0x0014BF] = new("Linksys", "Router"),
            [0x0018E7] = new("Cameo / D-Link", "Router"),
            [0x001E58] = new("D-Link", "Router"),
            [0x002401] = new("D-Link", "Router"),
            [0x14D64D] = new("D-Link", "Router"),
            [0x00095B] = new("Netgear", "Router"),
            [0x00146C] = new("Netgear", "Router"),
            [0x20E52A] = new("Netgear", "Router"),
            [0x9C3DCF] = new("Netgear", "Router"),
            [0x001D0F] = new("TP-Link", "Router"),
            [0x14CC20] = new("TP-Link", "Router"),
            [0x30B5C2] = new("TP-Link", "Router"),
            [0x50C7BF] = new("TP-Link", "Router"),
            [0x60E327] = new("TP-Link", "Router"),
            [0x8416F9] = new("TP-Link", "Router"),
            [0x98DAC4] = new("TP-Link", "Router"),
            [0xB0BE76] = new("TP-Link", "Router"),
            [0xC025E9] = new("TP-Link", "Router"),
            [0xE8DE27] = new("TP-Link", "Router"),
            [0xF4F26D] = new("TP-Link", "Router"),
            [0x000C42] = new("MikroTik", "Router"),
            [0x4C5E0C] = new("MikroTik", "Router"),
            [0xE48D8C] = new("MikroTik", "Router"),
            [0x00156D] = new("Ubiquiti", "Router"),
            [0x24A43C] = new("Ubiquiti", "Router"),
            [0x44D9E7] = new("Ubiquiti", "Router"),
            [0x7483C2] = new("Ubiquiti", "Router"),
            [0x802AA8] = new("Ubiquiti", "Router"),
            [0xF09FC2] = new("Ubiquiti", "Router"),
            [0x001A2B] = new("Ayecom / Cisco", "Router"),
            [0x001B54] = new("Cisco", "Router"),
            [0x00259C] = new("Cisco Linksys", "Router"),
            [0x0418D6] = new("Ubiquiti", "Router"),
            [0xB4FBE4] = new("Ubiquiti", "Router"),

            // PC / Workstation NICs (Intel, Realtek, ASUS, MSI, Gigabyte, Dell, HP, Lenovo)
            [0x001B21] = new("Intel", "Workstation"),
            [0x001E67] = new("Intel", "Workstation"),
            [0x00216A] = new("Intel", "Workstation"),
            [0x3C970E] = new("Intel", "Workstation"),
            [0x4851B7] = new("Intel", "Workstation"),
            [0x6805CA] = new("Intel", "Workstation"),
            [0x7C7A91] = new("Intel", "Workstation"),
            [0x843A4B] = new("Intel", "Workstation"),
            [0x8C8CAA] = new("Intel", "Workstation"),
            [0xA434D9] = new("Intel", "Workstation"),
            [0xACED5C] = new("Intel", "Workstation"),
            [0xDCFB48] = new("Intel", "Workstation"),
            [0xF8633F] = new("Intel", "Workstation"),
            [0x00E04C] = new("Realtek", "Workstation"),
            [0x52544C] = new("Realtek", "Workstation"),
            [0x7085C2] = new("Realtek", "Workstation"),
            [0x001FC6] = new("ASUSTek", "Workstation"),
            [0x049226] = new("ASUSTek", "Workstation"),
            [0x04D4C4] = new("ASUSTek", "Workstation"),
            [0x107B44] = new("ASUSTek", "Workstation"),
            [0x2C56DC] = new("ASUSTek", "Workstation"),
            [0x38D547] = new("ASUSTek", "Workstation"),
            [0xAC220B] = new("ASUSTek", "Workstation"),
            [0xF02F74] = new("ASUSTek", "Workstation"),
            [0x00D861] = new("Micro-Star (MSI)", "Workstation"),
            [0x2CF05D] = new("Micro-Star (MSI)", "Workstation"),
            [0x309C23] = new("Micro-Star (MSI)", "Workstation"),
            [0x4CCC6A] = new("Micro-Star (MSI)", "Workstation"),
            [0xD8BBC1] = new("Micro-Star (MSI)", "Workstation"),
            [0x1C1B0D] = new("Gigabyte", "Workstation"),
            [0x74D435] = new("Gigabyte", "Workstation"),
            [0xB42E99] = new("Gigabyte", "Workstation"),
            [0xE0D55E] = new("Gigabyte", "Workstation"),
            [0x7085C2] = new("ASRock", "Workstation"),
            [0xA8A159] = new("ASRock", "Workstation"),
            [0x001422] = new("Dell", "Workstation"),
            [0x180373] = new("Dell", "Workstation"),
            [0xD4BED9] = new("Dell", "Workstation"),
            [0xF8B156] = new("Dell", "Workstation"),
            [0x3C5282] = new("HP", "Workstation"),
            [0x98E7F4] = new("HP", "Workstation"),
            [0xFC15B4] = new("HP", "Workstation"),
            [0x54EE75] = new("Lenovo", "Workstation"),
            [0x98FA9B] = new("Lenovo", "Workstation"),
            [0xE86A64] = new("Lenovo", "Workstation"),

            // Apple (iPhone, iPad, Mac)
            [0x000393] = new("Apple", "Mobile"),
            [0x0017F2] = new("Apple", "Mobile"),
            [0x001CB3] = new("Apple", "Mobile"),
            [0x002500] = new("Apple", "Mobile"),
            [0x18AF61] = new("Apple", "Mobile"),
            [0x28CFE9] = new("Apple", "Mobile"),
            [0x3C0754] = new("Apple", "Mobile"),
            [0x40A6D9] = new("Apple", "Mobile"),
            [0x5855CA] = new("Apple", "Mobile"),
            [0x600308] = new("Apple", "Mobile"),
            [0x705681] = new("Apple", "Mobile"),
            [0x7CF05F] = new("Apple", "Mobile"),
            [0x8866A5] = new("Apple", "Mobile"),
            [0x907240] = new("Apple", "Mobile"),
            [0xA45E60] = new("Apple", "Mobile"),
            [0xACBC32] = new("Apple", "Mobile"),
            [0xBC52B7] = new("Apple", "Mobile"),
            [0xC869CD] = new("Apple", "Mobile"),
            [0xD02598] = new("Apple", "Mobile"),
            [0xDC2B2A] = new("Apple", "Mobile"),
            [0xF01898] = new("Apple", "Mobile"),
            [0xF45C89] = new("Apple", "Mobile"),

            // Samsung, Xiaomi, Huawei, Google, OnePlus
            [0x001247] = new("Samsung", "Mobile"),
            [0x001599] = new("Samsung", "Mobile"),
            [0x10D542] = new("Samsung", "Mobile"),
            [0x28987B] = new("Samsung", "Mobile"),
            [0x38AA3C] = new("Samsung", "Mobile"),
            [0x5001BB] = new("Samsung", "Mobile"),
            [0x5C0A5B] = new("Samsung", "Mobile"),
            [0x78471D] = new("Samsung", "Mobile"),
            [0x8425DB] = new("Samsung", "Mobile"),
            [0x94350A] = new("Samsung", "Mobile"),
            [0xBC1485] = new("Samsung", "Mobile"),
            [0xD022BE] = new("Samsung", "Mobile"),
            [0xE47CF9] = new("Samsung", "Mobile"),
            [0x009EC8] = new("Xiaomi", "Mobile"),
            [0x14F65A] = new("Xiaomi", "Mobile"),
            [0x286C07] = new("Xiaomi", "Mobile"),
            [0x34CE00] = new("Xiaomi", "Mobile"),
            [0x640980] = new("Xiaomi", "Mobile"),
            [0x7811DC] = new("Xiaomi", "Mobile"),
            [0x9C99A0] = new("Xiaomi", "Mobile"),
            [0xF8A45F] = new("Xiaomi", "Mobile"),
            [0x001882] = new("Huawei", "Mobile"),
            [0x00E0FC] = new("Huawei", "Mobile"),
            [0x2469A5] = new("Huawei", "Mobile"),
            [0x48DB50] = new("Huawei", "Mobile"),
            [0x8853D4] = new("Huawei", "Mobile"),
            [0x3C5AB4] = new("Google", "Mobile"),
            [0x546009] = new("Google", "Mobile"),
            [0xF4F5D8] = new("Google", "Mobile"),
            [0x94652D] = new("OnePlus", "Mobile"),
            [0xC0EEFB] = new("OnePlus", "Mobile"),

            // Consoles, Smart TVs & Media
            [0x0013A9] = new("Sony PlayStation", "Media/Console"),
            [0x0019C5] = new("Sony PlayStation", "Media/Console"),
            [0x00D9D1] = new("Sony PlayStation", "Media/Console"),
            [0x709E29] = new("Sony PlayStation", "Media/Console"),
            [0xFC0FE6] = new("Sony PlayStation", "Media/Console"),
            [0x0009BF] = new("Nintendo", "Media/Console"),
            [0x58BDA3] = new("Nintendo", "Media/Console"),
            [0x7CBB8A] = new("Nintendo", "Media/Console"),
            [0x98B6E9] = new("Nintendo", "Media/Console"),
            [0x0050F2] = new("Microsoft Xbox", "Media/Console"),
            [0x7CED8D] = new("Microsoft Xbox", "Media/Console"),
            [0x000918] = new("Samsung SmartTV", "Media/Console"),
            [0x00E091] = new("LG Electronics", "Media/Console"),
            [0x388C50] = new("LG SmartTV", "Media/Console"),
            [0xB8BBBF] = new("Roku", "Media/Console"),

            // IoT & Embedded (Raspberry Pi, Espressif, Amazon)
            [0xB827EB] = new("Raspberry Pi", "IoT"),
            [0xDC2632] = new("Raspberry Pi", "IoT"),
            [0xE45F01] = new("Raspberry Pi", "IoT"),
            [0x240AC4] = new("Espressif (ESP32)", "IoT"),
            [0x30AEA4] = new("Espressif (ESP32)", "IoT"),
            [0x84F3EB] = new("Espressif (ESP8266)", "IoT"),
            [0xA4CF12] = new("Espressif", "IoT"),
            [0x44650D] = new("Amazon Echo/Fire", "IoT"),
            [0x6854FD] = new("Amazon Devices", "IoT"),
            [0xFC65DE] = new("Amazon Devices", "IoT"),
            [0x001132] = new("Synology NAS", "Workstation")
        };

        return dict.ToFrozenDictionary();
    }
}
