namespace TerrariaModder.Core.Net
{
    /// <summary>
    /// Custom packet IDs used by the TerrariaModder framework.
    /// Terraria 1.4.5 uses up to MessageID 161 (MessageID.Count = 162).
    /// ID 250 is safely above this range and is intercepted via Harmony
    /// before Terraria's switch statement can reject or drop it.
    /// </summary>
    public static class PacketIds
    {
        public const byte TerrariaModder = 250;
    }

    /// <summary>
    /// Sub-type discriminator bytes for TerrariaModder packets (payload byte 0).
    /// </summary>
    public static class PacketSubTypes
    {
        /// <summary>Server → client on connect. Sends all [Server] config values.</summary>
        public const byte ServerConfigSync = 0x01;

        /// <summary>Client → server. Requests a [Server] property change.</summary>
        public const byte ConfigChangeRequest = 0x02;

        /// <summary>Server → all clients. Accepted [Server] property change.</summary>
        public const byte ConfigChangeBroadcast = 0x03;

        /// <summary>Server → client. Change request was rejected (RestartRequired or invalid).</summary>
        public const byte ConfigChangeRejected = 0x04;

        // ---- Phase 4: Identity + Permissions ----

        /// <summary>Client → server on connect. Client sends its stable install GUID.</summary>
        public const byte IdentityAnnounce = 0x10;

        /// <summary>Server → client. Client's role (Admin/Player) and per-mod grants for this session.</summary>
        public const byte PermissionSync = 0x11;

        /// <summary>Server → admin clients. Updated connected player list (name, slot, role).</summary>
        public const byte PlayerListUpdate = 0x12;

        // ---- Phase 5: Mod List ----

        /// <summary>Server → client on connect. Sends active mod list for compatibility validation.</summary>
        public const byte ModListExchange = 0x20;

        /// <summary>Client → server after ModListExchange. Client announces its own mods so server can validate no extra Required mods (G1) and version match (G2).</summary>
        public const byte ModListClientAnnounce = 0x21;

        // ---- Phase I: Heterogeneous mod set (TypeIdManifest) ----

        /// <summary>Server → client after successful mod list exchange. Sends type IDs for Optional mods so client can display placeholder items.</summary>
        public const byte TypeIdManifest = 0x52;

        // ---- Phase 7: Server commands ----

        /// <summary>Client → server. Admin command (time set, item spawn, player op, etc.).</summary>
        public const byte ServerCommandRequest = 0x40;

        /// <summary>Server → client. Result of a ServerCommandRequest.</summary>
        public const byte ServerCommandResponse = 0x41;

        // ---- Phase 7b: Storage ----

        /// <summary>Client → server. Take/deposit/craft operation on StorageHub.</summary>
        public const byte StorageRequest = 0x30;

        /// <summary>Server → client. Result of a StorageRequest.</summary>
        public const byte StorageResponse = 0x31;

        // ---- Phase 8: Server-authoritative player moddata (H4) ----

        /// <summary>Server → client on join. Server's copy of the client's custom items.</summary>
        public const byte CustomItemSync = 0x50;

        /// <summary>Client → server on save. Client's current custom item inventory snapshot.</summary>
        public const byte CustomItemSave = 0x51;

        /// <summary>Bidirectional ping for M1 round-trip probe.</summary>
        public const byte Ping = 0xFF;
    }
}
