using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Numerics;

using Buffer = Silk.NET.Vulkan.Buffer;

#nullable enable

namespace VMASharp
{
    using Defragmentation;
    using VMASharp;

    public sealed unsafe class VulkanMemoryAllocator : IDisposable
    {
        const long SmallHeapMaxSize = 1024L * 1024 * 1024;

        internal Vk VkApi { get; private set; }

        internal readonly Device Device;
        internal readonly Instance Instance;

        internal readonly Version32 VulkanAPIVersion;

        internal bool UseExtMemoryBudget;
        internal bool UseAMDDeviceCoherentMemory;

        internal uint HeapSizeLimitMask;

        internal PhysicalDeviceProperties physicalDeviceProperties;
        internal PhysicalDeviceMemoryProperties memoryProperties;

        internal readonly BlockList[] BlockLists = new BlockList[Vk.MaxMemoryTypes]; //Default Pools

        internal DedicatedAllocationHandler[] DedicatedAllocations = new DedicatedAllocationHandler[Vk.MaxMemoryTypes];

        private long PreferredLargeHeapBlockSize;
        private PhysicalDevice PhysicalDevice;
        private int CurrentFrame;
        private uint GPUDefragmentationMemoryTypeBits = uint.MaxValue;

        private readonly ReaderWriterLockSlim PoolsMutex = new ReaderWriterLockSlim();
        private readonly List<VulkanMemoryPool> Pools = new List<VulkanMemoryPool>();
        internal uint NextPoolID;

        internal CurrentBudgetData Budget = new CurrentBudgetData();

        public VulkanMemoryAllocator(in VulkanMemoryAllocatorCreateInfo createInfo)
        {
            if (createInfo.VulkanAPIObject == null)
            {
                throw new ArgumentNullException(nameof(createInfo.VulkanAPIObject), "API vtable is null");
            }

            this.VkApi = createInfo.VulkanAPIObject;

            if (createInfo.Instance.Handle == default)
            {
                throw new ArgumentNullException("createInfo.Instance");
            }

            if (createInfo.LogicalDevice.Handle == default)
            {
                throw new ArgumentNullException("createInfo.LogicalDevice");
            }

            if (createInfo.PhysicalDevice.Handle == default)
            {
                throw new ArgumentNullException("createInfo.PhysicalDevice");
            }

            if (!VkApi.CurrentInstance.HasValue || createInfo.Instance.Handle != VkApi.CurrentInstance.Value.Handle)
            {
                throw new ArgumentException("API Instance does not match the Instance passed with 'createInfo'.");
            }

            if (!VkApi.CurrentDevice.HasValue || createInfo.LogicalDevice.Handle != VkApi.CurrentDevice.Value.Handle)
            {
                throw new ArgumentException("API Device does not match the Instance passed with 'createInfo'.");
            }

            if (createInfo.VulkanAPIVersion < Vk.Version11)
            {
                throw new NotSupportedException("Vulkan API Version of less than 1.1 is not supported");
            }

            this.Instance = createInfo.Instance;
            this.PhysicalDevice = createInfo.PhysicalDevice;
            this.Device = createInfo.LogicalDevice;

            this.VulkanAPIVersion = createInfo.VulkanAPIVersion;

            if (this.VulkanAPIVersion == 0)
            {
                this.VulkanAPIVersion = Vk.Version10;
            }

            this.UseExtMemoryBudget = createInfo.UseExtMemoryBudget;

            VkApi.GetPhysicalDeviceProperties(createInfo.PhysicalDevice, out this.physicalDeviceProperties);
            VkApi.GetPhysicalDeviceMemoryProperties(createInfo.PhysicalDevice, out this.memoryProperties);

            Debug.Assert(Helpers.IsPow2(Helpers.DebugAlignment));
            Debug.Assert(Helpers.IsPow2(Helpers.DebugMinBufferImageGranularity));
            Debug.Assert(Helpers.IsPow2((long)this.PhysicalDeviceProperties.Limits.BufferImageGranularity));
            Debug.Assert(Helpers.IsPow2((long)this.PhysicalDeviceProperties.Limits.NonCoherentAtomSize));

            this.PreferredLargeHeapBlockSize = (createInfo.PreferredLargeHeapBlockSize != 0) ? createInfo.PreferredLargeHeapBlockSize : (256L * 1024 * 1024);

            this.GlobalMemoryTypeBits = this.CalculateGlobalMemoryTypeBits();

            if (createInfo.HeapSizeLimits != null)
            {
                Span<MemoryHeap> memoryHeaps = MemoryMarshal.CreateSpan(ref this.memoryProperties.MemoryHeaps_0, (int)Vk.MaxMemoryHeaps);

                int heapLimitLength = Math.Min(createInfo.HeapSizeLimits.Length, (int)Vk.MaxMemoryHeaps);

                for (int heapIndex = 0; heapIndex < heapLimitLength; ++heapIndex)
                {
                    long limit = createInfo.HeapSizeLimits[heapIndex];

                    if (limit <= 0)
                    {
                        continue;
                    }

                    this.HeapSizeLimitMask |= 1u << heapIndex;
                    ref MemoryHeap heap = ref memoryHeaps[heapIndex];

                    if ((ulong)limit < heap.Size)
                    {
                        heap.Size = (ulong)limit;
                    }
                }
            }

            for (int memTypeIndex = 0; memTypeIndex < this.MemoryTypeCount; ++memTypeIndex)
            {
                long preferredBlockSize = this.CalcPreferredBlockSize(memTypeIndex);

                this.BlockLists[memTypeIndex] =
                    new BlockList(this, null, memTypeIndex, preferredBlockSize, 0, int.MaxValue, this.BufferImageGranularity, createInfo.FrameInUseCount, false, Helpers.DefaultMetaObjectCreate);

                ref DedicatedAllocationHandler alloc = ref DedicatedAllocations[memTypeIndex];

                alloc.Allocations = new List<Allocation>();
                alloc.Mutex = new ReaderWriterLockSlim();
            }

            if (UseExtMemoryBudget)
            {
                UpdateVulkanBudget();
            }
        }

        public int CurrentFrameIndex { get; set; }

        internal long BufferImageGranularity
        {
            get
            {
                return (long)Math.Max(1, PhysicalDeviceProperties.Limits.BufferImageGranularity);
            }
        }

        internal int MemoryHeapCount => (int)MemoryProperties.MemoryHeapCount;

        internal int MemoryTypeCount => (int)MemoryProperties.MemoryTypeCount;

        internal bool IsIntegratedGPU
        {
            get => this.PhysicalDeviceProperties.DeviceType == PhysicalDeviceType.IntegratedGpu;
        }

        internal uint GlobalMemoryTypeBits { get; private set; }


        public void Dispose()
        {
            if (this.Pools.Count != 0)
            {
                throw new InvalidOperationException("");
            }

            int i = this.MemoryTypeCount;

            while (i-- != 0)
            {
                if (this.DedicatedAllocations[i].Allocations.Count != 0)
                {
                    throw new InvalidOperationException("Unfreed dedicatedAllocations found");
                }

                this.BlockLists[i].Dispose();
            }
        }

        public ref readonly PhysicalDeviceProperties PhysicalDeviceProperties => ref this.physicalDeviceProperties;

        public ref readonly PhysicalDeviceMemoryProperties MemoryProperties => ref this.memoryProperties;

        public MemoryPropertyFlags GetMemoryTypeProperties(int memoryTypeIndex)
        {
            return this.MemoryType(memoryTypeIndex).PropertyFlags;
        }

        public int? FindMemoryTypeIndex(uint memoryTypeBits, in AllocationCreateInfo allocInfo)
        {
            memoryTypeBits &= this.GlobalMemoryTypeBits;

            if (allocInfo.MemoryTypeBits != 0)
            {
                memoryTypeBits &= allocInfo.MemoryTypeBits;
            }

            MemoryPropertyFlags requiredFlags = allocInfo.RequiredFlags, preferredFlags = allocInfo.PreferredFlags, notPreferredFlags = default;

            switch (allocInfo.Usage)
            {
                case MemoryUsage.Unknown:
                    break;
                case MemoryUsage.GPU_Only:
                    if (this.IsIntegratedGPU || (preferredFlags & MemoryPropertyFlags.MemoryPropertyHostVisibleBit) == 0)
                    {
                        preferredFlags |= MemoryPropertyFlags.MemoryPropertyDeviceLocalBit;
                    }
                    break;
                case MemoryUsage.CPU_Only:
                    requiredFlags |= MemoryPropertyFlags.MemoryPropertyHostVisibleBit | MemoryPropertyFlags.MemoryPropertyHostCoherentBit;
                    break;
                case MemoryUsage.CPU_To_GPU:
                    requiredFlags |= MemoryPropertyFlags.MemoryPropertyHostVisibleBit;
                    if (!this.IsIntegratedGPU || (preferredFlags & MemoryPropertyFlags.MemoryPropertyHostVisibleBit) == 0)
                    {
                        preferredFlags |= MemoryPropertyFlags.MemoryPropertyDeviceLocalBit;
                    }
                    break;
                case MemoryUsage.GPU_To_CPU:
                    requiredFlags |= MemoryPropertyFlags.MemoryPropertyHostVisibleBit;
                    preferredFlags |= MemoryPropertyFlags.MemoryPropertyHostCachedBit;
                    break;
                case MemoryUsage.CPU_Copy:
                    notPreferredFlags |= MemoryPropertyFlags.MemoryPropertyDeviceLocalBit;
                    break;
                case MemoryUsage.GPU_LazilyAllocated:
                    requiredFlags |= MemoryPropertyFlags.MemoryPropertyLazilyAllocatedBit;
                    break;
                default:
                    throw new ArgumentException("Invalid Usage Flags");
            }

            if (((allocInfo.RequiredFlags | allocInfo.PreferredFlags) & (MemoryPropertyFlags.MemoryPropertyDeviceCoherentBitAmd | MemoryPropertyFlags.MemoryPropertyDeviceUncachedBitAmd)) == 0)
            {
                notPreferredFlags |= MemoryPropertyFlags.MemoryPropertyDeviceCoherentBitAmd;
            }

            int? memoryTypeIndex = null;
            int minCost = int.MaxValue;
            uint memTypeBit = 1;

            for (int memTypeIndex = 0; memTypeIndex < this.MemoryTypeCount; ++memTypeIndex, memTypeBit <<= 1)
            {
                if ((memTypeBit & memoryTypeBits) == 0)
                    continue;

                var currFlags = this.MemoryType(memTypeIndex).PropertyFlags;

                if ((requiredFlags & ~currFlags) != 0)
                    continue;

                int currCost = BitOperations.PopCount((uint)(preferredFlags & ~currFlags));
                 
                currCost += BitOperations.PopCount((uint)(currFlags & notPreferredFlags));

                if (currCost < minCost)
                {
                    if (currCost == 0)
                    {
                        return memTypeIndex;
                    }

                    memoryTypeIndex = memTypeIndex;
                    minCost = currCost;
                }
            }

            return memoryTypeIndex;
        }

        public int? FindMemoryTypeIndexForBufferInfo(in BufferCreateInfo bufferInfo, in AllocationCreateInfo allocInfo)
        {
            Buffer buffer;
            fixed (BufferCreateInfo* pBufferInfo = &bufferInfo)
            {
                var res = VkApi.CreateBuffer(this.Device, pBufferInfo, null, &buffer);

                if (res != Result.Success)
                {
                    throw new VulkanResultException(res);
                }
            }

            MemoryRequirements memReq;
            VkApi.GetBufferMemoryRequirements(this.Device, buffer, &memReq);

            var tmp = this.FindMemoryTypeIndex(memReq.MemoryTypeBits, in allocInfo);

            VkApi.DestroyBuffer(this.Device, buffer, null);

            return tmp;
        }

        public int? FindMemoryTypeIndexForImageInfo(in ImageCreateInfo imageInfo, in AllocationCreateInfo allocInfo)
        {
            Image image;
            fixed (ImageCreateInfo* pImageInfo = &imageInfo)
            {
                var res = VkApi.CreateImage(this.Device, pImageInfo, null, &image);

                if (res != Result.Success)
                {
                    throw new VulkanResultException(res);
                }
            }

            MemoryRequirements memReq;
            VkApi.GetImageMemoryRequirements(this.Device, image, &memReq);

            var tmp = this.FindMemoryTypeIndex(memReq.MemoryTypeBits, in allocInfo);

            VkApi.DestroyImage(this.Device, image, null);

            return tmp;
        }

        public Allocation AllocateMemory(in MemoryRequirements requirements, in AllocationCreateInfo createInfo)
        {
            return this.AllocateMemory(in requirements, false, false, default, default, in createInfo, SuballocationType.Unknown);
        }

        public Allocation AllocateMemoryForBuffer(Buffer buffer, in AllocationCreateInfo createInfo)
        {
            this.GetBufferMemoryRequirements(buffer, out MemoryRequirements memReq, out bool requireDedicatedAlloc, out bool prefersDedicatedAlloc);

            return this.AllocateMemory(in memReq, requireDedicatedAlloc, prefersDedicatedAlloc, buffer, default, in createInfo, SuballocationType.Buffer);
        }

        public Allocation AllocateMemoryForImage(Image image, in AllocationCreateInfo createInfo)
        {
            this.GetImageMemoryRequirements(image, out var memReq, out bool requireDedicatedAlloc, out bool preferDedicatedAlloc);

            return this.AllocateMemory(in memReq, requireDedicatedAlloc, preferDedicatedAlloc, default, image, in createInfo, SuballocationType.Image_Unknown);
        }

        public Result CheckCorruption(uint memoryTypeBits)
        {
            throw new NotImplementedException();
        }

        public Buffer CreateBuffer(in BufferCreateInfo bufferInfo, in AllocationCreateInfo allocInfo, out Allocation allocation)
        {
            Result res;
            Buffer buffer;

            Allocation? alloc = null;

            fixed (BufferCreateInfo* pInfo = &bufferInfo)
            {
                res = VkApi.CreateBuffer(this.Device, pInfo, null, &buffer);

                if (res < 0)
                {
                    throw new AllocationException("Buffer creation failed", res);
                }
            }

            try
            {
                alloc = this.AllocateMemoryForBuffer(buffer, allocInfo);
            }
            catch
            {
                VkApi.DestroyBuffer(this.Device, buffer, null);
                throw;
            }
            
            if ((allocInfo.Flags & AllocationCreateFlags.DontBind) == 0)
            {
                res = alloc.BindBufferMemory(buffer);

                if (res != Result.Success)
                {
                    VkApi.DestroyBuffer(this.Device, buffer, null);
                    alloc.Dispose();

                    throw new AllocationException("Unable to bind memory to buffer", res);
                }
            }

            allocation = alloc;

            return buffer;
        }

        public Image CreateImage(in ImageCreateInfo imageInfo, in AllocationCreateInfo allocInfo, out Allocation allocation)
        {
            if (imageInfo.Extent.Width == 0 ||
                imageInfo.Extent.Height == 0 ||
                imageInfo.Extent.Depth == 0 || 
                imageInfo.MipLevels == 0 ||
                imageInfo.ArrayLayers == 0)
            {
                throw new ArgumentException("Invalid Image Info");
            }

            Result res;
            Image image;
            Allocation alloc;

            fixed (ImageCreateInfo* pInfo = &imageInfo)
            {
                res = VkApi.CreateImage(this.Device, pInfo, null, &image);

                if (res < 0)
                {
                    throw new AllocationException("Image creation failed", res);
                }
            }

            SuballocationType suballocType = imageInfo.Tiling == ImageTiling.Optimal ? SuballocationType.Image_Optimal : SuballocationType.Image_Linear;

            try
            {
                alloc = this.AllocateMemoryForImage(image, allocInfo);
            }
            catch
            {
                VkApi.DestroyImage(this.Device, image, null);
                throw;
            }

            if ((allocInfo.Flags & AllocationCreateFlags.DontBind) == 0)
            {
                res = alloc.BindImageMemory(image);

                if (res != Result.Success)
                {
                    VkApi.DestroyImage(this.Device, image, null);
                    alloc.Dispose();

                    throw new AllocationException("Unable to Bind memory to image", res);
                }
            }

            allocation = alloc;

            return image;
        }

        private ref MemoryType MemoryType(int index)
        {
            Debug.Assert((uint)index < Vk.MaxMemoryTypes);

            return ref Unsafe.Add(ref this.memoryProperties.MemoryTypes_0, index);
        }

        private ref MemoryHeap MemoryHeap(int index)
        {
            Debug.Assert((uint)index < Vk.MaxMemoryHeaps);

            return ref Unsafe.Add(ref this.memoryProperties.MemoryHeaps_0, index);
        }

        internal int MemoryTypeIndexToHeapIndex(int typeIndex)
        {
            Debug.Assert(typeIndex < this.MemoryProperties.MemoryTypeCount);
            return (int)MemoryType(typeIndex).HeapIndex;
        }

        internal bool IsMemoryTypeNonCoherent(int memTypeIndex)
        {
            return (MemoryType(memTypeIndex).PropertyFlags & (MemoryPropertyFlags.MemoryPropertyHostVisibleBit | MemoryPropertyFlags.MemoryPropertyHostCoherentBit)) == MemoryPropertyFlags.MemoryPropertyHostVisibleBit;
        }

        internal long GetMemoryTypeMinAlignment(int memTypeIndex)
        {
            return IsMemoryTypeNonCoherent(memTypeIndex) ? (long)Math.Max(1, this.PhysicalDeviceProperties.Limits.NonCoherentAtomSize) : 1;
        }

        internal void GetBufferMemoryRequirements(Buffer buffer, out MemoryRequirements memReq, out bool requiresDedicatedAllocation, out bool prefersDedicatedAllocation)
        {
            var req = new BufferMemoryRequirementsInfo2
            {
                SType = StructureType.BufferMemoryRequirementsInfo2,
                Buffer = buffer
            };

            var dedicatedRequirements = new MemoryDedicatedRequirements
            {
                SType = StructureType.MemoryDedicatedRequirements,
            };

            var memReq2 = new MemoryRequirements2
            {
                SType = StructureType.MemoryRequirements2,
                PNext = &dedicatedRequirements
                };

                VkApi.GetBufferMemoryRequirements2(this.Device, &req, &memReq2);

                memReq = memReq2.MemoryRequirements;
            requiresDedicatedAllocation = dedicatedRequirements.RequiresDedicatedAllocation != 0;
            prefersDedicatedAllocation = dedicatedRequirements.PrefersDedicatedAllocation != 0;
        }

        internal void GetImageMemoryRequirements(Image image, out MemoryRequirements memReq, out bool requiresDedicatedAllocation, out bool prefersDedicatedAllocation)
        {
            var req = new ImageMemoryRequirementsInfo2
            {
                SType = StructureType.ImageMemoryRequirementsInfo2,
                Image = image
            };

            var dedicatedRequirements = new MemoryDedicatedRequirements
            {
                SType = StructureType.MemoryDedicatedRequirements,
            };

            var memReq2 = new MemoryRequirements2
            {
                SType = StructureType.MemoryRequirements2,
                PNext = &dedicatedRequirements
                };

                VkApi.GetImageMemoryRequirements2(this.Device, &req, &memReq2);

                memReq = memReq2.MemoryRequirements;
            requiresDedicatedAllocation = dedicatedRequirements.RequiresDedicatedAllocation != 0;
            prefersDedicatedAllocation = dedicatedRequirements.PrefersDedicatedAllocation != 0;
        }

        internal Allocation[] AllocateMemory(in MemoryRequirements memReq, bool requiresDedicatedAllocation,
            bool prefersDedicatedAllocation, Buffer dedicatedBuffer, Image dedicatedImage,
            in AllocationCreateInfo createInfo, SuballocationType suballocType, int allocationCount)
        {
            Debug.Assert(Helpers.IsPow2((long)memReq.Alignment));

            if (memReq.Size == 0)
                throw new ArgumentException("Allocation size cannot be 0");

            const AllocationCreateFlags CheckFlags1 = AllocationCreateFlags.DedicatedMemory | AllocationCreateFlags.NeverAllocate;
            const AllocationCreateFlags CheckFlags2 = AllocationCreateFlags.Mapped | AllocationCreateFlags.CanBecomeLost;

            if ((createInfo.Flags & CheckFlags1) == CheckFlags1)
            {
                throw new ArgumentException("Specifying AllocationCreateFlags.DedicatedMemory with AllocationCreateFlags.NeverAllocate is invalid");
            }
            else if ((createInfo.Flags & CheckFlags2) == CheckFlags2)
            {
                throw new ArgumentException("Specifying AllocationCreateFlags.Mapped with AllocationCreateFlags.CanBecomeLost is invalid");
            }

            if (requiresDedicatedAllocation)
            {
                if ((createInfo.Flags & AllocationCreateFlags.NeverAllocate) != 0)
                {
                    throw new AllocationException("AllocationCreateFlags.NeverAllocate specified while dedicated allocation required", Result.ErrorOutOfDeviceMemory);
                }

                if (createInfo.Pool != null)
                {
                    throw new ArgumentException("Pool specified while dedicated allocation required");
                }
            }

            if (createInfo.Pool != null && (createInfo.Flags & AllocationCreateFlags.DedicatedMemory) != 0)
            {
                throw new ArgumentException("Specified AllocationCreateFlags.DedicatedMemory when createInfo.Pool is not null");
            }

            if (createInfo.Pool != null)
            {
                int memoryTypeIndex = createInfo.Pool.BlockList.MemoryTypeIndex;
                long alignmentForPool = Math.Max((long)memReq.Alignment, this.GetMemoryTypeMinAlignment(memoryTypeIndex));

                AllocationCreateInfo infoForPool = createInfo;

                if ((createInfo.Flags & AllocationCreateFlags.Mapped) != 0 && !this.MemoryType(memoryTypeIndex).PropertyFlags.HasFlag(MemoryPropertyFlags.MemoryPropertyHostVisibleBit))
                {
                    infoForPool.Flags &= ~AllocationCreateFlags.Mapped;
                }

                return createInfo.Pool.BlockList.Allocate(this.CurrentFrameIndex, (long)memReq.Size, alignmentForPool, infoForPool, suballocType, allocationCount);
            }
            else
            {
                uint memoryTypeBits = memReq.MemoryTypeBits;
                var typeIndex = this.FindMemoryTypeIndex(memoryTypeBits, createInfo);

                if (typeIndex == null)
                {
                    throw new AllocationException("Unable to find suitable memory type for allocation", Result.ErrorFeatureNotPresent);
                }

                long alignmentForType = Math.Max((long)memReq.Alignment, this.GetMemoryTypeMinAlignment((int)typeIndex));

                return this.AllocateMemoryOfType((long)memReq.Size, alignmentForType, requiresDedicatedAllocation | prefersDedicatedAllocation, dedicatedBuffer, dedicatedImage, createInfo, (int)typeIndex, suballocType, allocationCount);
            }
        }

        internal Allocation AllocateMemory(in MemoryRequirements memReq, bool requiresDedicatedAllocation,
            bool prefersDedicatedAllocation, Buffer dedicatedBuffer, Image dedicatedImage,
            in AllocationCreateInfo createInfo, SuballocationType suballocType)
        {
            Debug.Assert(Helpers.IsPow2((long)memReq.Alignment));

            if (memReq.Size == 0)
                throw new ArgumentException("Allocation size cannot be 0");

            const AllocationCreateFlags CheckFlags1 = AllocationCreateFlags.DedicatedMemory | AllocationCreateFlags.NeverAllocate;
            const AllocationCreateFlags CheckFlags2 = AllocationCreateFlags.Mapped | AllocationCreateFlags.CanBecomeLost;

            if ((createInfo.Flags & CheckFlags1) == CheckFlags1)
            {
                throw new ArgumentException("Specifying AllocationCreateFlags.DedicatedMemory with AllocationCreateFlags.NeverAllocate is invalid");
            }
            else if ((createInfo.Flags & CheckFlags2) == CheckFlags2)
            {
                throw new ArgumentException("Specifying AllocationCreateFlags.Mapped with AllocationCreateFlags.CanBecomeLost is invalid");
            }

            if (requiresDedicatedAllocation)
            {
                if ((createInfo.Flags & AllocationCreateFlags.NeverAllocate) != 0)
                {
                    throw new AllocationException("AllocationCreateFlags.NeverAllocate specified while dedicated allocation required", Result.ErrorOutOfDeviceMemory);
                }

                if (createInfo.Pool != null)
                {
                    throw new ArgumentException("Pool specified while dedicated allocation required");
                }
            }

            if (createInfo.Pool != null && (createInfo.Flags & AllocationCreateFlags.DedicatedMemory) != 0)
            {
                throw new ArgumentException("Specified AllocationCreateFlags.DedicatedMemory when createInfo.Pool is not null");
            }

            if (createInfo.Pool != null)
            {
                int memoryTypeIndex = createInfo.Pool.BlockList.MemoryTypeIndex;
                long alignmentForPool = Math.Max((long)memReq.Alignment, this.GetMemoryTypeMinAlignment(memoryTypeIndex));

                AllocationCreateInfo infoForPool = createInfo;

                if ((createInfo.Flags & AllocationCreateFlags.Mapped) != 0 && (this.MemoryType(memoryTypeIndex).PropertyFlags & MemoryPropertyFlags.MemoryPropertyHostVisibleBit) == 0)
                {
                    infoForPool.Flags &= ~AllocationCreateFlags.Mapped;
                }

                return createInfo.Pool.BlockList.Allocate(this.CurrentFrameIndex, (long)memReq.Size, alignmentForPool, infoForPool, suballocType);
            }
            else
            {
                uint memoryTypeBits = memReq.MemoryTypeBits;
                var typeIndex = this.FindMemoryTypeIndex(memoryTypeBits, createInfo);

                if (typeIndex == null)
                {
                    throw new AllocationException("Unable to find suitable memory type for allocation", Result.ErrorFeatureNotPresent);
                }

                long alignmentForType = Math.Max((long)memReq.Alignment, this.GetMemoryTypeMinAlignment((int)typeIndex));

                return this.AllocateMemoryOfType((long)memReq.Size, alignmentForType, requiresDedicatedAllocation | prefersDedicatedAllocation, dedicatedBuffer, dedicatedImage, createInfo, (int)typeIndex, suballocType);
            }
        }

        public void FreeMemory(Allocation allocation)
        {
            if (allocation is null)
            {
                throw new ArgumentNullException(nameof(allocation));
            }

            if (allocation.TouchAllocation())
            {
                if (allocation is BlockAllocation blockAlloc)
                {
                    BlockList list;
                    var pool = blockAlloc.Block.ParentPool;
                    
                    if (pool != null)
                    {
                        list = pool.BlockList;
                    }
                    else
                    {
                        list = this.BlockLists[allocation.MemoryTypeIndex];
                        Debug.Assert(list != null);
                    }

                    list.Free(allocation);
                }
                else
                {
                    var dedicated = allocation as DedicatedAllocation;

                    Debug.Assert(dedicated != null);

                    FreeDedicatedMemory(dedicated);
                }
            }

            this.Budget.RemoveAllocation(this.MemoryTypeIndexToHeapIndex(allocation.MemoryTypeIndex), allocation.Size);
        }

        public Stats CalculateStats()
        {
            var newStats = new Stats();

            for (int i = 0; i < this.MemoryTypeCount; ++i)
            {
                var list = this.BlockLists[i];

                Debug.Assert(list != null);

                list.AddStats(newStats);
            }

            this.PoolsMutex.EnterReadLock();
            try
            {
                foreach (var pool in this.Pools)
                {
                    pool.BlockList.AddStats(newStats);
                }
            }
            finally
            {
                this.PoolsMutex.ExitReadLock();
            }

            for (int typeIndex = 0; typeIndex < this.MemoryTypeCount; ++typeIndex)
            {
                int heapIndex = this.MemoryTypeIndexToHeapIndex(typeIndex);

                ref DedicatedAllocationHandler handler = ref DedicatedAllocations[typeIndex];

                handler.Mutex.EnterReadLock();

                try
                {
                    foreach (var alloc in handler.Allocations)
                    {
                        ((DedicatedAllocation)alloc).CalcStatsInfo(out var stat);

                        StatInfo.Add(ref newStats.Total, stat);
                        StatInfo.Add(ref newStats.MemoryType[typeIndex], stat);
                        StatInfo.Add(ref newStats.MemoryHeap[heapIndex], stat);
                    }
                }
                finally
                {
                    handler.Mutex.ExitReadLock();
                }
            }

            newStats.PostProcess();

            return newStats;
        }

        internal void GetBudget(int heapIndex, out AllocationBudget outBudget)
        {
            outBudget = new AllocationBudget();

            if ((uint)heapIndex >= Vk.MaxMemoryHeaps)
            {
                throw new ArgumentOutOfRangeException(nameof(heapIndex));
            }

            if (this.UseExtMemoryBudget)
            {
                //Reworked to remove recursion
                if (this.Budget.OperationsSinceBudgetFetch >= 30)
                {
                    this.UpdateVulkanBudget(); //Outside of mutex lock
                }

                this.Budget.BudgetMutex.EnterReadLock();
                try
                {
                    ref var heapBudget = ref this.Budget.BudgetData[heapIndex];

                    outBudget.BlockBytes = heapBudget.BlockBytes;
                    outBudget.AllocationBytes = heapBudget.AllocationBytes;

                    if (heapBudget.VulkanUsage + outBudget.BlockBytes > heapBudget.BlockBytesAtBudgetFetch)
                    {
                        outBudget.Usage = heapBudget.VulkanUsage + outBudget.BlockBytes - heapBudget.BlockBytesAtBudgetFetch;
                    }
                    else
                    {
                        outBudget.Usage = 0;
                    }

                    outBudget.Budget = Math.Min(heapBudget.VulkanBudget, (long)this.MemoryHeap(heapIndex).Size);
                }
                finally
                {
                    this.Budget.BudgetMutex.ExitReadLock();
                }
            }
            else
            {
                ref var heapBudget = ref this.Budget.BudgetData[heapIndex];

                outBudget.BlockBytes = heapBudget.BlockBytes;
                outBudget.AllocationBytes = heapBudget.AllocationBytes;

                outBudget.Usage = heapBudget.BlockBytes;
                outBudget.Budget = (long)(this.MemoryHeap(heapIndex).Size * 8 / 10); //80% heuristics
            }
        }

        internal void GetBudget(int firstHeap, AllocationBudget[] outBudgets)
        {
            Debug.Assert(outBudgets != null && outBudgets.Length != 0);
            Array.Clear(outBudgets, 0, outBudgets.Length);

            if ((uint)(outBudgets.Length + firstHeap) >= Vk.MaxMemoryHeaps)
            {
                throw new ArgumentOutOfRangeException();
            }

            if (this.UseExtMemoryBudget)
            {
                //Reworked to remove recursion
                if (this.Budget.OperationsSinceBudgetFetch >= 30)
                {
                    this.UpdateVulkanBudget(); //Outside of mutex lock
                }

                this.Budget.BudgetMutex.EnterReadLock();
                try
                {
                    for (int i = 0; i < outBudgets.Length; ++i)
                    {
                        int heapIndex = i + firstHeap;
                        ref AllocationBudget outBudget = ref outBudgets[i];

                        ref var heapBudget = ref this.Budget.BudgetData[heapIndex];

                        outBudget.BlockBytes = heapBudget.BlockBytes;
                        outBudget.AllocationBytes = heapBudget.AllocationBytes;

                        if (heapBudget.VulkanUsage + outBudget.BlockBytes > heapBudget.BlockBytesAtBudgetFetch)
                        {
                            outBudget.Usage = heapBudget.VulkanUsage + outBudget.BlockBytes - heapBudget.BlockBytesAtBudgetFetch;
                        }
                        else
                        {
                            outBudget.Usage = 0;
                        }

                        outBudget.Budget = Math.Min(heapBudget.VulkanBudget, (long)this.MemoryHeap(heapIndex).Size);
                    }
                }
                finally
                {
                    this.Budget.BudgetMutex.ExitReadLock();
                }
            }
            else
            {
                for (int i = 0; i < outBudgets.Length; ++i)
                {
                    int heapIndex = i + firstHeap;
                    ref AllocationBudget outBudget = ref outBudgets[i];
                    ref var heapBudget = ref this.Budget.BudgetData[heapIndex];

                    outBudget.BlockBytes = heapBudget.BlockBytes;
                    outBudget.AllocationBytes = heapBudget.AllocationBytes;

                    outBudget.Usage = heapBudget.BlockBytes;
                    outBudget.Budget = (long)(this.MemoryHeap(heapIndex).Size * 8 / 10); //80% heuristics
                }
            }
        }

        internal Result DefragmentationBegin(in DefragmentationInfo2 info, DefragmentationStats stats, DefragmentationContext context)
        {
            throw new NotImplementedException();
        }

        internal Result DefragmentationEnd(DefragmentationContext context)
        {
            throw new NotImplementedException();
        }

        internal Result DefragmentationPassBegin(ref DefragmentationPassMoveInfo[] passInfo, DefragmentationContext context)
        {
            throw new NotImplementedException();
        }

        internal Result DefragmentationPassEnd(DefragmentationContext context)
        {
            throw new NotImplementedException();
        }

        public VulkanMemoryPool CreatePool(in AllocationPoolCreateInfo createInfo)
        {
            var tmpCreateInfo = createInfo;

            if (tmpCreateInfo.MaxBlockCount == 0)
            {
                tmpCreateInfo.MaxBlockCount = int.MaxValue;
            }

            if (tmpCreateInfo.MinBlockCount > tmpCreateInfo.MaxBlockCount)
            {
                throw new ArgumentException("Min block count is higher than max block count");
            }

            if (tmpCreateInfo.MemoryTypeIndex >= this.MemoryTypeCount || ((1u << tmpCreateInfo.MemoryTypeIndex) & this.GlobalMemoryTypeBits) == 0)
            {
                throw new ArgumentException("Invalid memory type index");
            }

            long preferredBlockSize = this.CalcPreferredBlockSize(tmpCreateInfo.MemoryTypeIndex);

            var pool = new VulkanMemoryPool(this, tmpCreateInfo, preferredBlockSize);

            this.PoolsMutex.EnterWriteLock();
            try 
            {
                this.Pools.Add(pool);
            }
            finally
            {
                this.PoolsMutex.ExitWriteLock();
            }

            return pool;
        }

        internal void DestroyPool(VulkanMemoryPool pool)
        {
            this.PoolsMutex.EnterWriteLock();
            try
            {
                bool success = this.Pools.Remove(pool);
                Debug.Assert(success, "Pool not found in allocator");
            }
            finally
            {
                this.PoolsMutex.ExitWriteLock();
            }
        }

        internal void GetPoolStats(VulkanMemoryPool pool, out PoolStats stats)
        {
            pool.BlockList.GetPoolStats(out stats);
        }

        internal int MakePoolAllocationsLost(VulkanMemoryPool pool)
        {
            return pool.BlockList.MakePoolAllocationsLost(this.CurrentFrame);
        }

        internal Result CheckPoolCorruption(VulkanMemoryPool pool)
        {
            throw new NotImplementedException();
        }

        internal Allocation CreateLostAllocation()
        {
            throw new NotImplementedException();
        }

        internal Result AllocateVulkanMemory(in MemoryAllocateInfo allocInfo, out DeviceMemory memory)
        {
            int heapIndex = this.MemoryTypeIndexToHeapIndex((int)allocInfo.MemoryTypeIndex);
            ref var budgetData = ref this.Budget.BudgetData[heapIndex];

            if ((this.HeapSizeLimitMask & (1u << heapIndex)) != 0)
            {
                long heapSize, blockBytes, blockBytesAfterAlloc;

                heapSize = (long)this.MemoryHeap(heapIndex).Size;

                do
                {
                    blockBytes = budgetData.BlockBytes;
                    blockBytesAfterAlloc = blockBytes + (long)allocInfo.AllocationSize;

                    if (blockBytesAfterAlloc > heapSize)
                    {
                        throw new AllocationException("Budget limit reached for heap index " + heapIndex, Result.ErrorOutOfDeviceMemory);
                    }
                }
                while (Interlocked.CompareExchange(ref budgetData.BlockBytes, blockBytesAfterAlloc, blockBytes) != blockBytes);
            }
            else
            {
                Interlocked.Add(ref budgetData.BlockBytes, (long)allocInfo.AllocationSize);
            }

            fixed (MemoryAllocateInfo* pInfo = &allocInfo)
            fixed (DeviceMemory* pMemory = &memory)
            {
                var res = VkApi.AllocateMemory(this.Device, pInfo, null, pMemory);

                if (res == Result.Success)
                {
                    Interlocked.Increment(ref this.Budget.OperationsSinceBudgetFetch);
                }
                else
                {
                    Interlocked.Add(ref budgetData.BlockBytes, -(long)allocInfo.AllocationSize);
                }

                return res;
            }
        }

        internal void FreeVulkanMemory(int memoryType, long size, DeviceMemory memory)
        {
            VkApi.FreeMemory(this.Device, memory, null);

            Interlocked.Add(ref this.Budget.BudgetData[this.MemoryTypeIndexToHeapIndex(memoryType)].BlockBytes, -size);
        }

        internal Result BindVulkanBuffer(Buffer buffer, DeviceMemory memory, long offset, void* pNext)
        {
            if (pNext != null)
            {
                var info = new BindBufferMemoryInfo
                {
                    SType = StructureType.BindBufferMemoryInfo,
                    PNext = pNext,
                        Buffer = buffer,
                        Memory = memory,
                        MemoryOffset = (ulong)offset
                    };

                return VkApi.BindBufferMemory2(this.Device, 1, &info);
            }
            else
            {
                return VkApi.BindBufferMemory(this.Device, buffer, memory, (ulong)offset);
            }
        }

        internal Result BindVulkanImage(Image image, DeviceMemory memory, long offset, void* pNext)
        {
            if (pNext != default)
            {
                var info = new BindImageMemoryInfo
                {
                    SType = StructureType.BindBufferMemoryInfo,
                    PNext = pNext,
                    Image = image,
                        Memory = memory,
                        MemoryOffset = (ulong)offset
                    };

                return VkApi.BindImageMemory2(this.Device, 1, &info);
            }
            else
            {
                return VkApi.BindImageMemory(this.Device, image, memory, (ulong)offset);
            }
        }

        internal void FillAllocation(Allocation allocation, byte pattern)
        {
            if (Helpers.DebugInitializeAllocations && !allocation.CanBecomeLost &&
                (this.MemoryType(allocation.MemoryTypeIndex).PropertyFlags & MemoryPropertyFlags.MemoryPropertyHostVisibleBit) != 0)
            {
                IntPtr pData = allocation.Map();

                Unsafe.InitBlockUnaligned(ref *(byte*)pData, pattern, (uint)allocation.Size);

                this.FlushOrInvalidateAllocation(allocation, 0, long.MaxValue, CacheOperation.Flush);

                allocation.Unmap();
            }
        }

        internal uint GetGPUDefragmentationMemoryTypeBits()
        {
            uint memTypeBits = this.GPUDefragmentationMemoryTypeBits;
            if (memTypeBits == uint.MaxValue)
            {
                memTypeBits = this.CalculateGpuDefragmentationMemoryTypeBits();
                this.GPUDefragmentationMemoryTypeBits = memTypeBits;
            }
            return memTypeBits;
        }

        private long CalcPreferredBlockSize(int memTypeIndex)
        {
            var heapIndex = this.MemoryTypeIndexToHeapIndex(memTypeIndex);

            Debug.Assert((uint)heapIndex < Vk.MaxMemoryHeaps);

            var heapSize = (long)Unsafe.Add(ref this.memoryProperties.MemoryHeaps_0, heapIndex).Size;

            return Helpers.AlignUp(heapSize <= SmallHeapMaxSize ? (heapSize / 8) : this.PreferredLargeHeapBlockSize, 32);
        }

        private Allocation[] AllocateMemoryOfType(long size, long alignment, bool dedicatedAllocation, Buffer dedicatedBuffer,
                                            Image dedicatedImage, in AllocationCreateInfo createInfo,
                                            int memoryTypeIndex, SuballocationType suballocType, int allocationCount)
        {
            var finalCreateInfo = createInfo;

            if ((finalCreateInfo.Flags & AllocationCreateFlags.Mapped) != 0 && (this.MemoryType(memoryTypeIndex).PropertyFlags & MemoryPropertyFlags.MemoryPropertyHostVisibleBit) == 0)
            {
                finalCreateInfo.Flags &= ~AllocationCreateFlags.Mapped;
            }

            if (finalCreateInfo.Usage == MemoryUsage.GPU_LazilyAllocated)
            {
                finalCreateInfo.Flags |= AllocationCreateFlags.DedicatedMemory;
            }

            var blockList = this.BlockLists[memoryTypeIndex];

            long preferredBlockSize = blockList.PreferredBlockSize;
            bool preferDedicatedMemory = dedicatedAllocation || size > preferredBlockSize / 2;

            if (preferDedicatedMemory && (finalCreateInfo.Flags & AllocationCreateFlags.NeverAllocate) == 0 && finalCreateInfo.Pool == null)
            {
                finalCreateInfo.Flags |= AllocationCreateFlags.DedicatedMemory;
            }

            Exception? blockAllocException = null;

            if ((finalCreateInfo.Flags & AllocationCreateFlags.DedicatedMemory) == 0)
            {
                try
                {
                    return blockList.Allocate(this.CurrentFrameIndex, size, alignment, finalCreateInfo, suballocType, allocationCount);
                }
                catch (Exception e) //Catch exception and attempt to allocate dedicated memory, this behavior may be changed for multi-allocations
                {
                    blockAllocException = e;
                }
            }

            //Try a dedicated allocation if a block allocation failed, or if specified as a dedicated allocation
            if ((finalCreateInfo.Flags & AllocationCreateFlags.NeverAllocate) != 0)
            {
                throw new AllocationException("Block List allocation failed, and `AllocationCreateFlags.NeverAllocate` specified", blockAllocException);
            }

            return this.AllocateDedicatedMemory(size,
                                                suballocType,
                                                memoryTypeIndex,
                                                (finalCreateInfo.Flags & AllocationCreateFlags.WithinBudget) != 0,
                                                (finalCreateInfo.Flags & AllocationCreateFlags.Mapped) != 0,
                                                finalCreateInfo.UserData,
                                                allocationCount);
        }

        private Allocation AllocateMemoryOfType(long size, long alignment, bool dedicatedAllocation, Buffer dedicatedBuffer,
                                            Image dedicatedImage, in AllocationCreateInfo createInfo,
                                            int memoryTypeIndex, SuballocationType suballocType)
        {
            var finalCreateInfo = createInfo;

            if ((finalCreateInfo.Flags & AllocationCreateFlags.Mapped) != 0 && (this.MemoryType(memoryTypeIndex).PropertyFlags & MemoryPropertyFlags.MemoryPropertyHostVisibleBit) == 0)
            {
                finalCreateInfo.Flags &= ~AllocationCreateFlags.Mapped;
            }

            if (finalCreateInfo.Usage == MemoryUsage.GPU_LazilyAllocated)
            {
                finalCreateInfo.Flags |= AllocationCreateFlags.DedicatedMemory;
            }

            var blockList = this.BlockLists[memoryTypeIndex];

            long preferredBlockSize = blockList.PreferredBlockSize;
            bool preferDedicatedMemory = dedicatedAllocation || size > preferredBlockSize / 2;

            if (preferDedicatedMemory && (finalCreateInfo.Flags & AllocationCreateFlags.NeverAllocate) == 0 && finalCreateInfo.Pool == null)
            {
                finalCreateInfo.Flags |= AllocationCreateFlags.DedicatedMemory;
            }

            Exception? blockAllocException = null;

            if ((finalCreateInfo.Flags & AllocationCreateFlags.DedicatedMemory) == 0)
            {
                try
                {
                    return blockList.Allocate(this.CurrentFrameIndex, size, alignment, finalCreateInfo, suballocType);
                }
                catch (Exception e)
                {
                    blockAllocException = e;
                }
            }

            //Try a dedicated allocation if a block allocation failed, or if specified as a dedicated allocation
            if ((finalCreateInfo.Flags & AllocationCreateFlags.NeverAllocate) != 0)
            {
                throw new AllocationException("Block List allocation failed, and `AllocationCreateFlags.NeverAllocate` specified", blockAllocException);
            }

            return this.AllocateDedicatedMemory(size,
                                                suballocType,
                                                memoryTypeIndex,
                                                (finalCreateInfo.Flags & AllocationCreateFlags.WithinBudget) != 0,
                                                (finalCreateInfo.Flags & AllocationCreateFlags.Mapped) != 0,
                                                finalCreateInfo.UserData,
                                                dedicatedBuffer,
                                                dedicatedImage);
        }

        private Allocation AllocateDedicatedMemoryPage(
            long size, SuballocationType suballocType, int memTypeIndex, in MemoryAllocateInfo allocInfo, bool map,
            object userData)
        {
            var res = this.AllocateVulkanMemory(allocInfo, out var memory);

            if (res < 0)
            {
                throw new AllocationException("Dedicated memory allocation Failed", res);
            }

            IntPtr mappedData = default;
            if (map)
            {
                res = VkApi.MapMemory(this.Device, memory, 0, Vk.WholeSize, 0, (void**)&mappedData);

                if (res < 0)
                {
                    this.FreeVulkanMemory(memTypeIndex, size, memory);

                    throw new AllocationException("Unable to map dedicated allocation", res);
                }
            }

            var allocation = new DedicatedAllocation(this, memTypeIndex, memory, suballocType, mappedData, size)
            {
                UserData = userData
            };

            this.Budget.AddAllocation(this.MemoryTypeIndexToHeapIndex(memTypeIndex), size);

            this.FillAllocation(allocation, Helpers.AllocationFillPattern_Created);

            return allocation;
        }

        private Allocation[] AllocateDedicatedMemory(long size, SuballocationType suballocType, int memTypeIndex, bool withinBudget, bool map, object userData, int allocationCount)
        {
            int heapIndex = this.MemoryTypeIndexToHeapIndex(memTypeIndex);

            if (withinBudget)
            {
                this.GetBudget(heapIndex, out var budget);
                if (budget.Usage + size * allocationCount > budget.Budget)
                {
                    throw new AllocationException("Memory Budget limit reached for heap index " + heapIndex, Result.ErrorOutOfDeviceMemory);
                }
            }

            MemoryAllocateInfo allocInfo = new MemoryAllocateInfo
            {
                MemoryTypeIndex = (uint)memTypeIndex,
                AllocationSize = (ulong)size
            };

            int allocIndex = 0;
            Result res = Result.Success;
            Allocation[] allocations = new Allocation[allocationCount];

            try
            {
                for (allocIndex = 0; allocIndex < allocations.Length; ++allocIndex)
                {
                    allocations[allocIndex] = this.AllocateDedicatedMemoryPage(size, suballocType, memTypeIndex, allocInfo, map, userData);

                    if (res != Result.Success)
                    {
                        break;
                    }
                }
            }
            catch
            {
                while (allocIndex > 0)
                {
                    allocIndex -= 1;

                    var alloc = allocations[allocIndex];

                    Debug.Assert(alloc != null);

                    var memory = alloc.Memory;

                    /*
                    There is no need to call this, because Vulkan spec allows to skip vkUnmapMemory
                    before vkFreeMemory.

                    if(currAlloc->GetMappedData() != VMA_NULL)
                    {
                        (*m_VulkanFunctions.vkUnmapMemory)(m_hDevice, hMemory);
                    }
                    */

                    this.FreeVulkanMemory(memTypeIndex, alloc.Size, memory);
                    this.Budget.RemoveAllocation(heapIndex, alloc.Size);
                }

                throw;
            }

            //Register made allocations
            ref DedicatedAllocationHandler handler = ref this.DedicatedAllocations[memTypeIndex];

            handler.Mutex.EnterWriteLock();
            try
            {
                foreach (var alloc in allocations)
                {
                    Debug.Assert(alloc != null);
                    handler.Allocations.InsertSorted(alloc, (alloc1, alloc2) => alloc1.Offset.CompareTo(alloc2.Offset));
                }
            }
            finally
            {
                handler.Mutex.ExitWriteLock();
            }

            return allocations;
        }

        private Allocation AllocateDedicatedMemory(long size, SuballocationType suballocType, int memTypeIndex, bool withinBudget, bool map, object userData, Buffer dedicatedBuffer, Image dedicatedImage)
        {
            int heapIndex = this.MemoryTypeIndexToHeapIndex(memTypeIndex);

            if (withinBudget)
            {
                this.GetBudget(heapIndex, out var budget);
                if (budget.Usage + size > budget.Budget)
                {
                    throw new AllocationException("Memory Budget limit reached for heap index " + heapIndex, Result.ErrorOutOfDeviceMemory);
                }
            }

            MemoryAllocateInfo allocInfo = new MemoryAllocateInfo
            {
                MemoryTypeIndex = (uint)memTypeIndex,
                AllocationSize = (ulong)size
            };

            Debug.Assert(!(dedicatedBuffer.Handle != default && dedicatedImage.Handle != default), "dedicated buffer and dedicated image were both specified");

            var dedicatedAllocInfo = new MemoryDedicatedAllocateInfo { SType = StructureType.MemoryDedicatedAllocateInfo };

            if (dedicatedBuffer.Handle != default)
            {
                    dedicatedAllocInfo.Buffer = dedicatedBuffer;
                    allocInfo.PNext = &dedicatedAllocInfo;
                }
                else if (dedicatedImage.Handle != default)
                {
                dedicatedAllocInfo.Image = dedicatedImage;
                allocInfo.PNext = &dedicatedAllocInfo;
            }

            var alloc = this.AllocateDedicatedMemoryPage(size, suballocType, memTypeIndex, allocInfo, map, userData);

            //Register made allocations
            ref DedicatedAllocationHandler handler = ref this.DedicatedAllocations[memTypeIndex];

            handler.Mutex.EnterWriteLock();
            try
            {
                handler.Allocations.InsertSorted(alloc, (alloc1, alloc2) => alloc1.Offset.CompareTo(alloc2.Offset));
            }
            finally
            {
                handler.Mutex.ExitWriteLock();
            }

            return alloc;
        }

        private void FreeDedicatedMemory(DedicatedAllocation allocation)
        {
            ref DedicatedAllocationHandler handler = ref this.DedicatedAllocations[allocation.MemoryTypeIndex];

            handler.Mutex.EnterWriteLock();

            try
            {
                bool success = handler.Allocations.Remove(allocation);

                Debug.Assert(success);
            }
            finally
            {
                handler.Mutex.ExitWriteLock();
            }

            FreeVulkanMemory(allocation.MemoryTypeIndex, allocation.Size, allocation.Memory);
        }

        private uint CalculateGpuDefragmentationMemoryTypeBits()
        {
            throw new NotImplementedException();
        }

        private const uint AMDVendorID = 0x1002;

        private uint CalculateGlobalMemoryTypeBits()
        {
            Debug.Assert(this.MemoryTypeCount > 0);

            uint memoryTypeBits = uint.MaxValue;

            if (this.PhysicalDeviceProperties.VendorID == AMDVendorID && !this.UseAMDDeviceCoherentMemory)
            {
                // Exclude memory types that have VK_MEMORY_PROPERTY_DEVICE_COHERENT_BIT_AMD.
                for (int index = 0; index < this.MemoryTypeCount; ++index)
                {
                    if ((this.MemoryType(index).PropertyFlags & MemoryPropertyFlags.MemoryPropertyDeviceCoherentBitAmd) != 0)
                    {
                        memoryTypeBits &= ~(1u << index);
                    }
                }
            }

            return memoryTypeBits;
        }

        private void UpdateVulkanBudget()
        {
            Debug.Assert(UseExtMemoryBudget);

            PhysicalDeviceMemoryBudgetPropertiesEXT budgetProps = new PhysicalDeviceMemoryBudgetPropertiesEXT(StructureType.PhysicalDeviceMemoryBudgetPropertiesExt);

            PhysicalDeviceMemoryProperties2 memProps = new PhysicalDeviceMemoryProperties2(StructureType.PhysicalDeviceMemoryProperties2, &budgetProps);

            VkApi.GetPhysicalDeviceMemoryProperties2(this.PhysicalDevice, &memProps);

            Budget.BudgetMutex.EnterWriteLock();
            try
            {
                for (int i = 0; i < MemoryHeapCount; ++i)
                {
                    ref var data = ref Budget.BudgetData[i];

                    data.VulkanUsage = (long)budgetProps.HeapUsage[i];
                    data.VulkanBudget = (long)budgetProps.HeapBudget[i];

                    data.BlockBytesAtBudgetFetch = data.BlockBytes;

                    // Some bugged drivers return the budget incorrectly, e.g. 0 or much bigger than heap size.

                    ref var heap = ref this.MemoryHeap(i);

                    if (data.VulkanBudget == 0)
                    {
                        data.VulkanBudget = (long)(heap.Size * 8 / 10);
                    }
                    else if ((ulong)data.VulkanBudget > heap.Size)
                    {
                        data.VulkanBudget = (long)heap.Size;
                    }

                    if (data.VulkanUsage == 0 && data.BlockBytesAtBudgetFetch > 0)
                    {
                        data.VulkanUsage = data.BlockBytesAtBudgetFetch;
                    }
                }

                Budget.OperationsSinceBudgetFetch = 0;
            }
            finally
            {
                Budget.BudgetMutex.ExitWriteLock();
            }
        }

        internal void FlushOrInvalidateAllocation(Allocation allocation, long offset, long size, CacheOperation op)
        {
            int memTypeIndex = allocation.MemoryTypeIndex;
            if (size > 0 && this.IsMemoryTypeNonCoherent(memTypeIndex))
            {
                long allocSize = allocation.Size;

                Debug.Assert((ulong)offset <= (ulong)allocSize);

                var nonCoherentAtomSize = (long)this.physicalDeviceProperties.Limits.NonCoherentAtomSize;

                MappedMemoryRange memRange = new MappedMemoryRange(memory: allocation.Memory);

                if (allocation is BlockAllocation blockAlloc)
                {
                    memRange.Offset = (ulong)Helpers.AlignDown(offset, nonCoherentAtomSize);

                    if (size == long.MaxValue)
                    {
                        size = allocSize - offset;
                    }
                    else
                    {
                        Debug.Assert(offset + size <= allocSize);
                    }

                    memRange.Size = (ulong)Helpers.AlignUp(size + (offset - (long)memRange.Offset), nonCoherentAtomSize);

                    long allocOffset = blockAlloc.Offset;

                    Debug.Assert(allocOffset % nonCoherentAtomSize == 0);

                    long blockSize = blockAlloc.Block.MetaData.Size;

                    memRange.Offset += (ulong)allocOffset;
                    memRange.Size = Math.Min(memRange.Size, (ulong)blockSize - memRange.Offset);
                }
                else if (allocation is DedicatedAllocation)
                {
                    memRange.Offset = (ulong)Helpers.AlignDown(offset, nonCoherentAtomSize);

                    if (size == long.MaxValue)
                    {
                        memRange.Size = (ulong)allocSize - memRange.Offset;
                    }
                    else
                    {
                        Debug.Assert(offset + size <= allocSize);

                        memRange.Size = (ulong)Helpers.AlignUp(size + (offset - (long)memRange.Offset), nonCoherentAtomSize);
                    }
                }
                else
                {
                    Debug.Assert(false);
                    throw new ArgumentException("allocation type is not BlockAllocation or DedicatedAllocation");
                }

                switch (op)
                {
                    case CacheOperation.Flush:
                        VkApi.FlushMappedMemoryRanges(this.Device, 1, &memRange);
                        break;
                    case CacheOperation.Invalidate:
                        VkApi.InvalidateMappedMemoryRanges(this.Device, 1, &memRange);
                        break;
                    default:
                        Debug.Assert(false);
                        throw new ArgumentException("Invalid Cache Operation value", nameof(op));
                }
            }
        }

        internal struct DedicatedAllocationHandler
        {
            public List<Allocation> Allocations;
            public ReaderWriterLockSlim Mutex;
        }
    }
}
