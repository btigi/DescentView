using ii.Ascend.Model;
using System;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DescentView
{
    public static class PofWireframeConverter
    {
        public static Model3DGroup BuildWireframe(PolyModel model, double lineThickness)
        {
            var group = new Model3DGroup();
            var lineMaterial = new DiffuseMaterial(new SolidColorBrush(Colors.LightGray));
            var backMaterial = new DiffuseMaterial(new SolidColorBrush(Colors.LightGray));

            var (minP, maxP) = GetBounds(model);
            int n = Math.Min(model.NumModels, PolyModel.MaxSubmodels);
            var maxDim = Math.Max(Math.Max(maxP.X - minP.X, maxP.Y - minP.Y), maxP.Z - minP.Z);
            var pivotCrossSize = Math.Max(lineThickness * 2, maxDim * 0.015);

            // Hierarchy lines (skeleton: parent → child)
            for (int i = 0; i < n; i++)
            {
                int parent = model.SubmodelParents[i];
                if (parent >= 0 && parent < n && parent != i)
                {
                    var p = VertexToPoint3D(model.SubmodelPnts[i]);
                    var pParent = VertexToPoint3D(model.SubmodelPnts[parent]);
                    var mesh1 = CreateEdgeMesh(pParent, p, lineThickness, new Vector3D(0, 1, 0));
                    var mesh2 = CreateEdgeMesh(pParent, p, lineThickness, new Vector3D(1, 0, 0));
                    if (mesh1 != null)
                        group.Children.Add(new GeometryModel3D(mesh1, lineMaterial) { BackMaterial = backMaterial });
                    if (mesh2 != null)
                        group.Children.Add(new GeometryModel3D(mesh2, lineMaterial) { BackMaterial = backMaterial });
                }
            }

            // Pivot markers: small crosses at each joint (no boxes)
            for (int i = 0; i < n; i++)
            {
                var p = VertexToPoint3D(model.SubmodelPnts[i]);
                AddPivotCross(group, p, pivotCrossSize, lineThickness * 0.5, lineMaterial, backMaterial);
            }

            return group;
        }

        public static (Point3D Min, Point3D Max) GetBounds(PolyModel model)
        {
            var minV = model.Mins;
            var maxV = model.Maxs;
            var (minX, minY, minZ) = minV.ToFloat();
            var (maxX, maxY, maxZ) = maxV.ToFloat();
            return (new Point3D(minX, minY, minZ), new Point3D(maxX, maxY, maxZ));
        }

        public static string GetModelInfo(PolyModel model)
        {
            var bounds = GetBounds(model);
            var size = new Vector3D(
                bounds.Max.X - bounds.Min.X,
                bounds.Max.Y - bounds.Min.Y,
                bounds.Max.Z - bounds.Min.Z);
            return $"POF Model: {model.NumModels} submodel(s)\n" +
                   $"Radius: {model.Rad / 65536.0:F1}, Model data: {model.ModelData?.Length ?? 0} bytes\n" +
                   $"Bounds: ({bounds.Min.X:F1}, {bounds.Min.Y:F1}, {bounds.Min.Z:F1}) to ({bounds.Max.X:F1}, {bounds.Max.Y:F1}, {bounds.Max.Z:F1})";
        }

        private static Point3D VertexToPoint3D(VmsVector v)
        {
            var (x, y, z) = v.ToFloat();
            return new Point3D(x, y, z);
        }

        private static void AddPivotCross(Model3DGroup group, Point3D center, double halfLength,
            double thickness, Material front, Material back)
        {
            // Three short lines through the pivot (X, Y, Z axes) so joints are visible without boxes
            var dx = new Point3D(center.X - halfLength, center.Y, center.Z);
            var dx2 = new Point3D(center.X + halfLength, center.Y, center.Z);
            var dy = new Point3D(center.X, center.Y - halfLength, center.Z);
            var dy2 = new Point3D(center.X, center.Y + halfLength, center.Z);
            var dz = new Point3D(center.X, center.Y, center.Z - halfLength);
            var dz2 = new Point3D(center.X, center.Y, center.Z + halfLength);

            foreach (var (a, b) in new[] { (dx, dx2), (dy, dy2), (dz, dz2) })
            {
                var mesh1 = CreateEdgeMesh(a, b, thickness, new Vector3D(0, 1, 0));
                var mesh2 = CreateEdgeMesh(a, b, thickness, new Vector3D(1, 0, 0));
                if (mesh1 != null)
                    group.Children.Add(new GeometryModel3D(mesh1, front) { BackMaterial = back });
                if (mesh2 != null)
                    group.Children.Add(new GeometryModel3D(mesh2, front) { BackMaterial = back });
            }
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
