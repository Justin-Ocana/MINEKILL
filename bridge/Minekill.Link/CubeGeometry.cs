using System.Collections.Generic;
using System.Numerics;

namespace Minekill.Link
{
    // A unit cube centred on the origin for Unity (left-handed, clockwise front faces), with three
    // submeshes: 0 top, 1 the four sides, 2 bottom. Kept free of Unity so it can be unit-tested.
    public static class CubeGeometry
    {
        public sealed class Data
        {
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<int>[] Triangles = { new List<int>(), new List<int>(), new List<int>() };
        }

        public static Data Build()
        {
            var d = new Data();
            void Face(Vector3 normal, Vector3 up, int sub)
            {
                // Screen-right for a viewer outside the face (left-handed): Cross(normal, up).
                Vector3 right = Vector3.Cross(normal, up);
                Vector3 c = normal * 0.5f;
                int i = d.Positions.Count;
                d.Positions.Add(c - right * 0.5f - up * 0.5f); d.Uvs.Add(new Vector2(0, 0));
                d.Positions.Add(c + right * 0.5f - up * 0.5f); d.Uvs.Add(new Vector2(1, 0));
                d.Positions.Add(c + right * 0.5f + up * 0.5f); d.Uvs.Add(new Vector2(1, 1));
                d.Positions.Add(c - right * 0.5f + up * 0.5f); d.Uvs.Add(new Vector2(0, 1));
                for (int k = 0; k < 4; k++) d.Normals.Add(normal);
                d.Triangles[sub].AddRange(new[] { i, i + 2, i + 1, i, i + 3, i + 2 });
            }
            Vector3 up = Vector3.UnitY, fwd = Vector3.UnitZ, rightAxis = Vector3.UnitX;
            Face(up, fwd, 0);
            Face(fwd, up, 1);
            Face(-fwd, up, 1);
            Face(rightAxis, up, 1);
            Face(-rightAxis, up, 1);
            Face(-up, -fwd, 2);
            return d;
        }
    }
}
