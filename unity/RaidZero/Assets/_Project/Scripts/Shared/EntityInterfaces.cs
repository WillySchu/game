using UnityEngine;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;


namespace RaidZero.Shared
{

    // Entities that should respond in some way to bullet hits.
    public interface IDamageable
    {
        void Hit(NetworkConnection source, string ammoDefID);
    }

    // Living entities with character controllers and movement stats.
    [RequireComponent(typeof(MoveController))]
    public abstract class LivingEntity : NetworkBehaviour
    {
        protected Utils.SyncedRandom _random;
        public Utils.SyncedRandom SyncedRandom => _random;
        private readonly SyncVar<uint> _baseSeed = new();

        protected MoveController _moveController;

        public void Awake()
        {
            _moveController = GetComponent<MoveController>();
        }

        public override void OnStartServer()
        {
            _baseSeed.Value = (uint)Random.Range(int.MinValue, int.MaxValue);
            ResetRandom();
        }

        public override void OnStartClient()
        {
            ResetRandom();
        }

        private void ResetRandom() => _random = new Utils.SyncedRandom(_baseSeed.Value);


    }
}
