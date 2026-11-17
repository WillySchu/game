using UnityEngine;

namespace RaidZero.Shared
{
    public class HitscanProjectileRender : MonoBehaviour
    {
        [SerializeField] private Material tracerMaterial;
        [SerializeField] private float width = 0.02f;
        [SerializeField] private float lifetime = 0.1f;
        [SerializeField] private Color color = Color.white;

        public void ShowTracer(Vector3 start, Vector3 end)
        {
            GameObject tracer = new GameObject("Tracer");
            LineRenderer lineRenderer = tracer.AddComponent<LineRenderer>();
            lineRenderer.material = tracerMaterial;
            lineRenderer.startWidth = width;
            lineRenderer.endWidth = width;
            lineRenderer.startColor = color;
            lineRenderer.endColor = color;
            lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lineRenderer.SetPosition(0, start);
            lineRenderer.SetPosition(1, end);
            Destroy(tracer, lifetime);
        }
    }
}
