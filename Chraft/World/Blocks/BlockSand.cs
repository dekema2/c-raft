﻿#region C#raft License
// This file is part of C#raft. Copyright C#raft Team 
// 
// C#raft is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.
#endregion
using Chraft.Entity;
using Chraft.Interfaces;
using Chraft.World.Blocks.Physics;

namespace Chraft.World.Blocks
{
    class BlockSand : BlockBase
    {
        public BlockSand()
        {
            Name = "Sand";
            Type = BlockData.Blocks.Sand;
            IsSolid = true;
            LootTable.Add(new ItemStack((short)Type, 1));
        }

        public override void NotifyDestroy(EntityBase entity, StructBlock sourceBlock, StructBlock targetBlock)
        {
            if ((targetBlock.Coords.WorldY - sourceBlock.Coords.WorldY) == 1 &&
                    targetBlock.Coords.WorldX == sourceBlock.Coords.WorldX &&
                    targetBlock.Coords.WorldZ == sourceBlock.Coords.WorldZ)
                StartPhysics(targetBlock);
            base.NotifyDestroy(entity, sourceBlock, targetBlock);
        }

        protected override void UpdateWorld(StructBlock block, bool isDestroyed = false)
        {
            base.UpdateWorld(block, isDestroyed);
            if (!isDestroyed && block.Coords.WorldY > 1)
                if (block.World.GetBlockId(block.Coords.WorldX, block.Coords.WorldY - 1, block.Coords.WorldZ) == (byte)BlockData.Blocks.Air)
                    StartPhysics(block);
        }

        protected void StartPhysics(StructBlock block)
        {
            Remove(block);
            FallingSand fsBlock = new FallingSand(block.World, new AbsWorldCoords(block.Coords.WorldX + 0.5, block.Coords.WorldY + 0.5, block.Coords.WorldZ + 0.5));
            fsBlock.Start();
            block.World.PhysicsBlocks.TryAdd(fsBlock.EntityId, fsBlock);
        }
    }
}
