using FishNet.Connection;
using RaidZero.Net;
using RaidZero.Shared.Raid;
using UnityEngine;

namespace RaidZero.Shared
{
    /// <summary>
    /// A stand-in AI that stands still and takes hits: what the F1 overlay's "Spawn target dummy" puts in front of
    /// you, until the real AI body exists. It's a raid participant like an AI will be (RaidServer.SpawnAI): it
    /// carries real gear (AIKits.TargetDummy), its armor wears, and at no health it leaves a body with that gear.
    ///
    /// Its own collider (on this object) is the torso; a <see cref="TargetDummyHitbox"/> on a child collider is
    /// the head. Shots reach it the way they reach a player, through <see cref="IDamageable"/>, and only count on
    /// the server, which knows its ID. Made by Raid Zero > AI > Create Target Dummy Prefab.
    /// </summary>
    public sealed class TargetDummy : MonoBehaviour, IDamageable, IRaidAI
    {
        private string _aiId;

        /// <summary>Its ID in the raid, on the server; null on clients.</summary>
        public string AIId => _aiId;

        public void SetUp(string aiId) => _aiId = aiId;

        /// <summary>A shot hit its body.</summary>
        public void Hit(NetworkConnection source, string ammoDefID) => Hit(source, ammoDefID, BodyPart.Torso);

        /// <summary>A shot hit it there: its hitboxes call this.</summary>
        public void Hit(NetworkConnection source, string ammoDefId, BodyPart part)
        {
            var raid = RaidServer.Instance;
            if (_aiId != null && raid != null)
            {
                raid.Hit(_aiId, raid.ActorIdOf(source), ammoDefId, part);
            }
        }
    }
}
