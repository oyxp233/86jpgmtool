using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Network;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DropService
    {
        internal DropService()
        {
        }

        internal static void WarmUpAbyssParty()
        {
            HellMonsterDropConfig.WarmUp();
        }

        internal MonsterDropResult GenerateAndRegister(DungeonRun run, MonsterDropRequest request)
        {
            if (run == null) return default;

            var slotCounter = run.SceneSlotCounter;

            var dropPool = MonsterDropTable.GetDropPool(request.MonsterCode);
            int areaMaterialId = AreaMaterialDropProvider.GetAreaMaterialItem(run.DungeonId);
            if (areaMaterialId > 0)
            {
                var extended = new List<MonsterDropTable.DropPoolEntry>();
                if (dropPool != null) extended.AddRange(dropPool);
                extended.Add(new MonsterDropTable.DropPoolEntry { ItemId = areaMaterialId, Weight = 100 });
                dropPool = extended;
            }

            var generator = new DropGenerator(run.RoomLcg);
            var result = generator.GenerateMonsterDrops(
                request.DropRateLevel, request.MonsterType, request.MonsterCode,
                run.Difficulty, request.DungeonBasisLevel,
                ref slotCounter, dropPool);

            run.SceneSlotCounter = slotCounter;
            RegisterDrops(run, result.drops);

            return new MonsterDropResult
            {
                GoldAmount = result.goldAmount,
                Drops = result.drops
            };
        }

        internal List<DropInfo> GenerateAbyssPartyAndRegister(DungeonRun run, AbyssPartyDropRequest request)
        {
            if (run == null) return new List<DropInfo>();

            var slotCounter = run.SceneSlotCounter;

            var drops = IndependentDropSystem.GenerateDrops(
                request.MonsterCode, run.Difficulty, request.DungeonBasisLevel,
                run.RoomLcg, ref slotCounter);

            if (request.IsLastGroupMonster && !request.IsAbyssMonsterScript)
            {
                var rewardDrops = HellMonsterDropConfig.GenerateSpecificEquipmentDrops(
                    run.RoomLcg,
                    request.DungeonMinimumLevel,
                    request.DungeonBasisLevel,
                    run.Difficulty,
                    request.AbyssPartyDifficulty,
                    request.RewardRollCount,
                    ref slotCounter);
                drops.AddRange(rewardDrops);
            }

            run.SceneSlotCounter = slotCounter;
            RegisterDrops(run, drops);
            return drops;
        }

        internal PickupResult TryPickup(DungeonRun run, ushort srcSlot, EnhancedClientSession session)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0
                || !InventoryContext.TryGetLease(characterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log($"[DropService] online inventory missing cid={characterId} sceneSlot={srcSlot}");
                return PickupResult.PersistenceFailed;
            }

            return TryPickup(run, srcSlot, lease);
        }

        internal PickupResult TryPickup(DungeonRun run, ushort srcSlot, InventoryLease lease)
        {
            if (run == null || !run.Drops.TryGetValue(srcSlot, out var drop))
                return PickupResult.NotFound;
            if (lease == null)
                return PickupResult.PersistenceFailed;

            var carryLimit = drop.IsGold
                ? InventoryGoldCarryLimitLoader.Load(lease.CharacterId)
                : int.MaxValue;
            lock (lease.SyncRoot)
            {
                if (drop.IsGold)
                {
                    var baseGold = (int)drop.StackCount;
                    var bonusPct = GetEquippedGoldBonus(lease.Inventory);
                    var extraGold = baseGold * bonusPct / 100;
                    if (!lease.Inventory.TryGrantGold(baseGold + extraGold, carryLimit, out var grantedGold, out _))
                        return PickupResult.PersistenceFailed;

                    run.Drops.Remove(srcSlot);
                    var grantedBaseGold = Math.Min(baseGold, grantedGold);
                    var grantedExtraGold = Math.Min(extraGold, Math.Max(0, grantedGold - grantedBaseGold));
                    return new PickupResult
                    {
                        Success = true,
                        IsGold = true,
                        GoldAmount = grantedGold,
                        ExtraGold = grantedExtraGold
                    };
                }

                var pickupCount = NormalizePickupCount(drop.StackCount);
                var pickedItemId = drop.Core != null ? drop.Core.ItemId : (int)drop.TemplateId;
                InventoryRewardGrantResult grant;
                bool inserted;
                if (drop.Core != null)
                {
                    inserted = InventoryRewardGrantService.TryInsertExisting(
                        lease.Inventory,
                        drop.Core.Copy(),
                        pickupCount,
                        out grant);
                }
                else
                {
                    inserted = InventoryRewardGrantService.TryCreateAndInsert(
                        lease.Inventory,
                        (int)drop.TemplateId,
                        ItemCreateReason.DungeonDrop,
                        pickupCount,
                        out grant);
                }

                if (!inserted || !grant.Success)
                    return PickupResult.InventoryFull;

                run.Drops.Remove(srcSlot);
                return new PickupResult
                {
                    Success = true,
                    IsGold = false,
                    InventorySlot = grant.SlotIndex,
                    PickedUpItemId = pickedItemId
                };
            }
        }

        private static void RegisterDrops(DungeonRun run, List<DropInfo> drops)
        {
            if (drops == null || drops.Count == 0) return;
            foreach (var drop in drops)
                run.Drops[drop.SceneSlot] = drop;
        }

        private static int NormalizePickupCount(uint stackCount)
        {
            if (stackCount == 0)
                return 1;

            return stackCount > int.MaxValue ? int.MaxValue : (int)stackCount;
        }

        private static int GetEquippedGoldBonus(InventoryService inventory)
        {
            if (inventory == null)
                return 0;

            var totalBonus = 0;
            foreach (var pair in inventory.GetItems(InventoryListType.Equipment))
            {
                var itemId = pair.Value?.ItemId ?? 0;
                if (GoldBonusEquipments.TryGetValue(itemId, out var bonus))
                    totalBonus += bonus;
            }

            return totalBonus;
        }

        private static readonly Dictionary<int, int> GoldBonusEquipments = new()
        {
            {100320775, 12},
            {24191, 10},
            {100341606, 30},
            {100331240, 10},
            {100331319, 3},
            {26626, 3},
            {26627, 4},
            {26341, 3},
            {26342, 4},
            {26115, 3},
            {104000181, 3},
            {101020286, 3},
            {101020526, 3},
            {109000133, 3}
        };
    }

    internal struct MonsterDropRequest
    {
        public int DropRateLevel;
        public int MonsterType;
        public int MonsterCode;
        public int DungeonBasisLevel;
    }

    internal struct AbyssPartyDropRequest
    {
        public int MonsterCode;
        public int DungeonMinimumLevel;
        public int DungeonBasisLevel;
        public byte AbyssPartyDifficulty;
        public int RewardRollCount;
        public bool IsLastGroupMonster;
        public bool IsAbyssMonsterScript;
    }

    internal struct MonsterDropResult
    {
        public int GoldAmount;
        public List<DropInfo> Drops;
    }

    internal struct PickupResult
    {
        public bool Success;
        public bool IsGold;
        public int GoldAmount;
        public int ExtraGold;
        public short InventorySlot;
        public int PickedUpItemId;
        public PickupFailReason FailReason;

        internal static readonly PickupResult NotFound = new PickupResult { FailReason = PickupFailReason.NotFound };
        internal static readonly PickupResult InventoryFull = new PickupResult { FailReason = PickupFailReason.InventoryFull };
        internal static readonly PickupResult PersistenceFailed = new PickupResult { FailReason = PickupFailReason.PersistenceFailed };
    }

    internal enum PickupFailReason : byte
    {
        None,
        NotFound,
        InventoryFull,
        PersistenceFailed
    }
}
