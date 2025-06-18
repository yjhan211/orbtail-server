// ReSharper disable All

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.common.data;

namespace network.common.data.models
{
    [MessagePackObject]
    public class SlotItem
    {
        [Key("itemId")] public int ItemId { get; set; }
        [Key("generationQueue")] public List<int> GenerationQueue { get; set; } // Generator 전용
    
        public SlotItem()
        {
            ItemId = 0;
            GenerationQueue = new List<int>();
        }
    
        public SlotItem(int itemId, List<int>? generationQueue = null)
        {
            ItemId = itemId;
            GenerationQueue = generationQueue ?? new List<int>();
        }
    
        public bool IsEmpty() => ItemId == 0;
        public bool IsGenerator() => ItemId > 0 && GameItemData.Get(ItemId)?.IsMaterial == true;
        public bool HasItemsToGenerate() => GenerationQueue.Count > 0;
        public int NextItemToGenerate() => GenerationQueue.Count > 0 ? GenerationQueue[0] : 0;
    }
    
    [MessagePackObject]
    public partial class CraftInfo : IMessagePackObject
    {
        [IgnoreMember] public const string HashKey = "CraftInfo";

        public CraftInfo()
        {
            PlayerId = 0;
            Manuals = new List<int>();
            Slots = new List<SlotItem>();
        }
        
        public CraftInfo(long playerId)
        {
            PlayerId = playerId;
            Manuals = new List<int>();
            Slots = new List<SlotItem>();
            for (var i = 1; i <= 42; i++)
            {
                Slots.Add(new SlotItem());
            }
        }
        
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("manuals")] public List<int> Manuals { get; set; }
        [Key("slots")] public List<SlotItem> Slots { get; set; }

        public int GetRandomEmptySlotIndex()
        {
            var emptySlotIndices = new List<int>();

            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].IsEmpty())
                {
                    emptySlotIndices.Add(i);
                }
            }

            if (emptySlotIndices.Count == 0)
            {
                return -1;
            }

            var random = new Random();
            int randomIndex = random.Next(0, emptySlotIndices.Count);

            return emptySlotIndices[randomIndex];
        }
        
        public bool GenerateItemFromQueue(int generatorSlotIndex, int targetSlotIndex)
        {
            if (generatorSlotIndex < 0 || generatorSlotIndex >= Slots.Count)
            {
                return false;
            }

            if (targetSlotIndex < 0 || targetSlotIndex >= Slots.Count)
            {
                return false;
            }
        
            var generatorSlot = Slots[generatorSlotIndex];
            if (!generatorSlot.IsGenerator() || !generatorSlot.HasItemsToGenerate())
            {
                return false;
            }

            if (!Slots[targetSlotIndex].IsEmpty())
            {
                return false;
            }
        
            var itemToGenerate = generatorSlot.GenerationQueue[0];
            generatorSlot.GenerationQueue.RemoveAt(0);
            
            // 타겟에 넣고
            Slots[targetSlotIndex] = new SlotItem(itemToGenerate, null);
            
            // 제너레이터 다 썼으면 빈 슬롯으로 교체
            if (!generatorSlot.HasItemsToGenerate())
            {
                Slots[generatorSlotIndex] = new SlotItem();
            }
        
            return true;
        }
    }
}