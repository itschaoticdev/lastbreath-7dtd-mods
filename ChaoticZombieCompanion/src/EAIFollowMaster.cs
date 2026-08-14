using GamePath;
using UnityEngine;
using UnityEngine.Scripting;

namespace ChaoticZombieCompanion
{
    /// <summary>
    /// Heel. The one piece of genuinely new AI in this mod - vanilla has no follow-a-player task,
    /// because vanilla has nothing that willingly follows a player.
    ///
    /// Modelled on EAIApproachDistraction, which is the closest vanilla analogue: walk to a thing,
    /// look at it on the way, repath on a jittered timer so a group does not repath in lockstep.
    ///
    /// Registered at priority 5 by ThrallRuntime.MakeThrall, i.e. below ApproachAndAttackTarget
    /// (4 on a vanilla zombie) so a fight always outranks heeling.
    /// </summary>
    [Preserve]
    public class EAIFollowMaster : EAIBase
    {
        /// <summary>Ticks until the next repath. Jittered so a pair do not repath together.</summary>
        private int pathRecalculateTicks;

        /// <summary>Set once a path has actually been produced, so a failed path can end the task.</summary>
        private bool hadPath;

        public override void Init(EntityAlive _theEntity)
        {
            base.Init(_theEntity);
            // Same mutex as the other movement tasks: this must not run alongside the chase.
            MutexBits = 3;
            executeDelay = 0.2f;
        }

        public override bool CanExecute()
        {
            EntityPlayer owner = ThrallRuntime.GetOwnerOf(theEntity);
            if (owner == null) return false;

            // Fighting wins. The scheduler would mostly handle this via priority, but checking here
            // stops the task even starting during a chase.
            if (theEntity.GetAttackTarget() != null) return false;
            if (theEntity.IsSleeping || theEntity.sleepingOrWakingUp) return false;

            float dist = theEntity.GetDistance(owner);

            // Too far to path sanely - usually the owner drove off, or took a lift up a prefab.
            // Pathing across half a chunk fails and the thrall is lost for good, so close the gap
            // directly instead.
            if (dist > ZcConfig.TeleportDistance)
            {
                TeleportToOwner(owner);
                return false;
            }

            return dist > ZcConfig.FollowDistance;
        }

        public override void Start()
        {
            hadPath = false;
            pathRecalculateTicks = 0;
            UpdatePath();
        }

        public override bool Continue()
        {
            EntityPlayer owner = ThrallRuntime.GetOwnerOf(theEntity);
            if (owner == null) return false;
            if (theEntity.GetAttackTarget() != null) return false;

            PathEntity path = theEntity.navigator != null ? theEntity.navigator.getPath() : null;
            if (hadPath && path == null) return false;

            // Hysteresis: stop closer than the distance that started the follow, otherwise the
            // thrall oscillates in and out of the task at exactly FollowDistance.
            return theEntity.GetDistance(owner) > ZcConfig.FollowDistance * 0.6f;
        }

        public override void Update()
        {
            EntityPlayer owner = ThrallRuntime.GetOwnerOf(theEntity);
            if (owner == null) return;

            if (theEntity.navigator != null && theEntity.navigator.getPath() != null) hadPath = true;

            if (theEntity.GetDistance(owner) > ZcConfig.TeleportDistance)
            {
                TeleportToOwner(owner);
                return;
            }

            theEntity.SetLookPosition(owner.getHeadPosition());

            if (--pathRecalculateTicks <= 0) UpdatePath();
        }

        public override void Reset()
        {
            if (theEntity.moveHelper != null) theEntity.moveHelper.Stop();
            theEntity.SetLookPosition(Vector3.zero);
        }

        private void UpdatePath()
        {
            EntityPlayer owner = ThrallRuntime.GetOwnerOf(theEntity);
            if (owner == null) return;
            if (PathFinderThread.Instance == null) return;
            if (PathFinderThread.Instance.IsCalculatingPath(theEntity.entityId)) return;

            pathRecalculateTicks = 20 + GetRandom(20);
            theEntity.FindPath(owner.position, theEntity.GetMoveSpeedAggro(), true, this);
        }

        /// <summary>
        /// Drops the thrall a couple of metres behind its owner, keeping the owner's Y rather than
        /// sampling terrain height - terrain height is wrong inside a POI or on an upper floor,
        /// which is exactly where a follower is most likely to fall behind.
        /// </summary>
        private void TeleportToOwner(EntityPlayer owner)
        {
            Vector3 back = -owner.transform.forward;
            Vector3 pos = owner.position + back * 2f + Vector3.up * 0.25f;
            theEntity.SetPosition(pos, true);
            if (theEntity.moveHelper != null) theEntity.moveHelper.Stop();
            ThrallRuntime.DebugLog("thrall " + theEntity.entityId + " caught up to " + owner.EntityName);
        }
    }
}
