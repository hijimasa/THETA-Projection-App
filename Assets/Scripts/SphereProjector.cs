using UnityEngine;

namespace ThetaProjection
{
    /// <summary>
    /// 視聴者を包む内向きの球メッシュを生成し、equirectangular 映像を投影する。
    /// UV は経度・緯度の等間隔マッピング(equirect 標準)。
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class SphereProjector : MonoBehaviour
    {
        [Tooltip("球の半径 [m]。単眼映像では視差がないため見え方はほぼ変わらない")]
        public float radius = 10f;

        [Range(16, 256)] public int longitudeSegments = 96;
        [Range(8, 128)] public int latitudeSegments = 48;

        [Tooltip("映像が鏡像(文字が左右反転)になっている場合に切り替える (THETA V 実機では true が正)")]
        public bool flipHorizontal = true;

        [Tooltip("映像が上下逆に見える場合に切り替える (RTSP ソースで必要になることがある)")]
        public bool flipVertical;

        [Tooltip("映像の正面方向を回すオフセット角 [deg]")]
        public float yawOffsetDegrees;

        private Material _material;

        private void Awake()
        {
            var shader = Resources.Load<Shader>("ThetaEquirect");
            if (shader == null)
            {
                Debug.LogError("[SphereProjector] Resources/ThetaEquirect.shader が見つかりません");
                enabled = false;
                return;
            }
            _material = new Material(shader);
            GetComponent<MeshRenderer>().sharedMaterial = _material;
            GetComponent<MeshFilter>().sharedMesh = BuildInvertedSphere(radius, longitudeSegments, latitudeSegments);
            ApplySettings();
        }

        private void OnValidate()
        {
            if (Application.isPlaying && _material != null)
                ApplySettings();
        }

        private void ApplySettings()
        {
            _material.SetFloat("_FlipU", flipHorizontal ? 1f : 0f);
            _material.SetFloat("_FlipV", flipVertical ? 1f : 0f);
            transform.rotation = Quaternion.Euler(0f, yawOffsetDegrees, 0f);
        }

        public void SetTexture(Texture texture)
        {
            if (_material != null)
                _material.mainTexture = texture;
        }

        /// <summary>
        /// 内側から見るための球メッシュを生成する。
        /// v=1 が天頂、v=0 が天底。u は経度方向(継ぎ目は lon=0)。
        /// </summary>
        private static Mesh BuildInvertedSphere(float radius, int lonSegments, int latSegments)
        {
            int vertexCount = (lonSegments + 1) * (latSegments + 1);
            var vertices = new Vector3[vertexCount];
            var uv = new Vector2[vertexCount];
            var triangles = new int[lonSegments * latSegments * 6];

            for (int lat = 0; lat <= latSegments; lat++)
            {
                // theta: 0 (天頂) → π (天底)
                float theta = Mathf.PI * lat / latSegments;
                float y = Mathf.Cos(theta);
                float ring = Mathf.Sin(theta);

                for (int lon = 0; lon <= lonSegments; lon++)
                {
                    float phi = 2f * Mathf.PI * lon / lonSegments;
                    int i = lat * (lonSegments + 1) + lon;
                    vertices[i] = new Vector3(ring * Mathf.Sin(phi), y, ring * Mathf.Cos(phi)) * radius;
                    // 内側から見て鏡像にならないよう u は経度の逆方向に振る
                    uv[i] = new Vector2(1f - (float)lon / lonSegments, 1f - (float)lat / latSegments);
                }
            }

            int t = 0;
            for (int lat = 0; lat < latSegments; lat++)
            {
                for (int lon = 0; lon < lonSegments; lon++)
                {
                    int a = lat * (lonSegments + 1) + lon;
                    int b = a + 1;
                    int c = a + lonSegments + 1;
                    int d = c + 1;
                    // 内向きの巻き順(シェーダ側は Cull Off なので保険)
                    triangles[t++] = a; triangles[t++] = b; triangles[t++] = c;
                    triangles[t++] = b; triangles[t++] = d; triangles[t++] = c;
                }
            }

            var mesh = new Mesh
            {
                name = "InvertedSphere",
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
                vertices = vertices,
                uv = uv,
                triangles = triangles
            };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
