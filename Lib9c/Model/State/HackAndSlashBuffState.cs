﻿using System;
using System.Collections.Generic;
using Bencodex.Types;
using Nekoyume.TableData.Crystal;
using System.Linq;

namespace Nekoyume.Model.State
{
    public class HackAndSlashBuffState : IState
    {
        public int StageId { get; }
        public int StarCount { get; private set; }
        public List<int> BuffIds { get; private set; }

        public HackAndSlashBuffState(int stageId)
        {
            StageId = stageId;
            StarCount = 0;
            BuffIds = new List<int>();
        }

        public HackAndSlashBuffState(List serialized)
        {
            StageId = serialized[0].ToInteger();
            StarCount = serialized[1].ToInteger();
            BuffIds = serialized[2].ToList(StateExtensions.ToInteger);
        }

        public void Update(int gotStarCount, CrystalStageBuffGachaSheet sheet)
        {
            StarCount = Math.Min(StarCount + gotStarCount, sheet[StageId].MaxStar);
        }

        public void Update(List<int> buffIds)
        {
            BuffIds = buffIds;
        }

        public IValue Serialize()
        {
            return List.Empty
                .Add(StageId.Serialize())
                .Add(StarCount.Serialize())
                .Add(BuffIds.Select(i => i.Serialize()).Serialize());
        }
    }
}
