using System.Linq;
using System.Numerics;
using Minekill.Link;
using Xunit;

namespace Minekill.Link.Tests
{
    public class CubeGeometryTests
    {
        // Unity draws a triangle's front where Cross(b - a, c - a) points (clockwise from the front
        // in its left-handed space; Mesh.RecalculateNormals uses the same rule). Every triangle must
        // face out of the cube, or the blocks would be invisible from outside.
        [Fact]
        public void EveryTriangleFacesOutward()
        {
            var d = CubeGeometry.Build();
            int checkedTris = 0;
            foreach (var tris in d.Triangles)
            {
                for (int t = 0; t < tris.Count; t += 3)
                {
                    Vector3 a = d.Positions[tris[t]], b = d.Positions[tris[t + 1]], c = d.Positions[tris[t + 2]];
                    Vector3 front = Vector3.Cross(b - a, c - a);
                    Vector3 centre = (a + b + c) / 3f;
                    Assert.True(Vector3.Dot(front, centre) > 0, $"triangle {t / 3} faces inward");
                    Assert.True(Vector3.Dot(front, d.Normals[tris[t]]) > 0, $"triangle {t / 3} disagrees with its normal");
                    checkedTris++;
                }
            }
            Assert.Equal(12, checkedTris);
        }

        [Fact]
        public void SubmeshesAreTopSidesBottom()
        {
            var d = CubeGeometry.Build();
            Assert.Equal(6, d.Triangles[0].Count);   // top: one face
            Assert.Equal(24, d.Triangles[1].Count);  // four sides
            Assert.Equal(6, d.Triangles[2].Count);   // bottom
            Assert.All(d.Triangles[0], i => Assert.Equal(Vector3.UnitY, d.Normals[i]));
            Assert.All(d.Triangles[2], i => Assert.Equal(-Vector3.UnitY, d.Normals[i]));
            Assert.All(d.Positions, p => Assert.True(MathF.Abs(p.X) <= 0.5f && MathF.Abs(p.Y) <= 0.5f && MathF.Abs(p.Z) <= 0.5f));
        }

        // Side textures must stand upright (a texture's top row on the block's top edge).
        [Fact]
        public void SideTexturesAreUpright()
        {
            var d = CubeGeometry.Build();
            foreach (int i in d.Triangles[1].Distinct())
            {
                Assert.Equal(d.Positions[i].Y > 0 ? 1f : 0f, d.Uvs[i].Y);
            }
        }

        private static class MathF
        {
            public static float Abs(float v) => System.Math.Abs(v);
        }
    }
}
