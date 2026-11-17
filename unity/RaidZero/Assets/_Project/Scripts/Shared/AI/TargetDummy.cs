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
    /// the server, which knows its ID. Made by Raid Zero > AI > Create Target Dummy Prefab. Spawn zones spawn it
    /// as their profile's prefab until the real AI body exists.
    /// </summary>
    public sealed class TargetDummy : MonoBehaviour, IDamageable, IRaidAI
    {
        private string _aiId;

        /// <summary>Its ID in the raid, on the server; null on clients.</summary>
        public string AIId => _aiId;

        /// <summary>The kind of AI it stands in for, if a spawn zone spawned it (on the server); null from the F1 overlay.</summary>
        public AIProfile Profile { get; private set; }

        /// <summary>The spawn zone it came from, on the server; null from the F1 overlay.</summary>
        public AISpawnZone Zone { get; private set; }

        /// <summary>Who it's with: its profile's faction, or looters' for an F1 overlay dummy.</summary>
        public Faction Faction => Profile != null ? Profile.Faction : Faction.Looters;

        public void SetUp(string aiId) => _aiId = aiId;

        /// <summary>The spawn zone that spawned it says what it is (before it's networked).</summary>
        public void Assign(AIProfile profile, AISpawnZone zone)
        {
            Profile = profile;
            Zone = zone;
        }

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
