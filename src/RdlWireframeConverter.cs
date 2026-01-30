using ii.Ascend.Model;
using System;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DescentView
{
    public static class RdlWireframeConverter
    {
        private const double VertexScale = 1.0 / 65536.0; // VmsVector fixed-point scale

        private static readonly (int A, int B)[] SegmentEdges =
        {
            (0, 1), (1, 2), (2, 3), (3, 0), // back face
            (4, 5), (5, 6), (6, 7), (7, 4), // front face
            (0, 4), (1, 5), (2, 6), (3, 7)   // connecting edges
        };

        public static Model3DGroup BuildWireframe(RdlFile rdl, double lineThickness)
        {
            var group = new Model3DGroup();
            var material = new DiffuseMaterial(new SolidColorBrush(Colors.LightGray));
            var backMaterial = new DiffuseMaterial(new SolidColorBrush(Colors.LightGray));

            foreach (var segment in rdl.Segments)
            {
                for (int e = 0; e < SegmentEdges.Length; e++)
                {
                    int i = SegmentEdges[e].A;
                    int j = SegmentEdges[e].B;
                    int vi = segment.Verts[i];
                    int vj = segment.Verts[j];
                    if (vi < 0 || vi >= rdl.Vertices.Count || vj < 0 || vj >= rdl.Vertices.Count)
                        continue;

                    var a = VertexToPoint3D(rdl.Vertices[vi]);
                    var b = VertexToPoint3D(rdl.Vertices[vj]);
                    var mesh1 = CreateEdgeMesh(a, b, lineThickness, new Vector3D(0, 1, 0));
                    var mesh2 = CreateEdgeMesh(a, b, lineThickness, new Vector3D(1, 0, 0));
                    if (mesh1 != null)
                    {
                        group.Children.Add(new GeometryModel3D(mesh1, material) { BackMaterial = backMaterial });
                    }
                    if (mesh2 != null)
                    {
                        group.Children.Add(new GeometryModel3D(mesh2, material) { BackMaterial = backMaterial });
                    }
                }
            }

            return group;
        }

        public static (Point3D Min, Point3D Max) GetBounds(RdlFile rdl)
        {
            if (rdl.Vertices.Count == 0)
                return (new Point3D(0, 0, 0), new Point3D(0, 0, 0));

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (var v in rdl.Vertices)
            {
                var (x, y, z) = v.ToFloat();
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                maxZ = Math.Max(maxZ, z);
            }

            return (new Point3D(minX, minY, minZ), new Point3D(maxX, maxY, maxZ));
        }

        public static string GetModelInfo(RdlFile rdl)
        {
            var bounds = GetBounds(rdl);
            return $"RDL Level: {rdl.LevelName}\n" +
                   $"Vertices: {rdl.Vertices.Count}, Segments: {rdl.Segments.Count}\n" +
                   $"Bounds: ({bounds.Min.X:F1}, {bounds.Min.Y:F1}, {bounds.Min.Z:F1}) to ({bounds.Max.X:F1}, {bounds.Max.Y:F1}, {bounds.Max.Z:F1})";
        }

        private static Point3D VertexToPoint3D(VmsVector v)
        {
            var (x, y, z) = v.ToFloat();
            return new Point3D(x, y, z);
        }

        private static MeshGeometry3D? CreateEdgeMesh(Point3D a, Point3D b, double halfThickness, Vector3D upHint)
        {
            var dir = new Vector3D(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
            var len = dir.Length;
            if (len < 1e-6)
                return null;

            dir.Normalize();

            var perp = Vector3D.CrossProduct(dir, upHint);
            if (perp.Length < 0.01)
                perp = Vector3D.CrossProduct(dir, new Vector3D(1, 0, 0));
            if (perp.Length < 0.01)
                perp = Vector3D.CrossProduct(dir, new Vector3D(0, 0, 1));
            perp.Normalize();
            perp *= halfThickness;

            var p0 = a + perp;
            var p1 = a - perp;
            var p2 = b - perp;
            var p3 = b + perp;

            var mesh = new MeshGeometry3D();
            mesh.Positions.Add(p0);
            mesh.Positions.Add(p1);
            mesh.Positions.Add(p2);
            mesh.Positions.Add(p3);
            mesh.TriangleIndices.Add(0);
            mesh.TriangleIndices.Add(1);
            mesh.TriangleIndices.Add(2);
            mesh.TriangleIndices.Add(0);
            mesh.TriangleIndices.Add(2);
            mesh.TriangleIndices.Add(3);

            return mesh;
        }
    }
}