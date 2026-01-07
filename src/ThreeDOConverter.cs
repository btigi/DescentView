using ii.CompleteDestruction.Model.ThreeDO;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DescentView
{
    public class ThreeDOConverter
    {
        private const double FixedPointScale = 65536.0;

        public static Model3DGroup ConvertToModel3DGroup(ThreeDOObject rootObject)
        {
            var modelGroup = new Model3DGroup();
            
            ProcessObject(rootObject, modelGroup, new Point3D(0, 0, 0));
            
            return modelGroup;
        }

        private static void ProcessObject(ThreeDOObject? obj, Model3DGroup modelGroup, Point3D parentPosition)
        {
            if (obj == null)
                return;

            var position = new Point3D(
                parentPosition.X + obj.XFromParent / FixedPointScale,
                parentPosition.Y + obj.YFromParent / FixedPointScale,
                parentPosition.Z + obj.ZFromParent / FixedPointScale
            );

            if (obj.Vertices != null && obj.Vertices.Length > 0 && 
                obj.Primitives != null && obj.Primitives.Length > 0)
            {
                var mesh = ConvertObjectToMesh(obj, position);
                if (mesh != null)
                {
                    var material = new DiffuseMaterial(new SolidColorBrush(Colors.LightGray));
                    var backMaterial = new DiffuseMaterial(new SolidColorBrush(Colors.Gray));
                    
                    var geometryModel = new GeometryModel3D
                    {
                        Geometry = mesh,
                        Material = material,
                        BackMaterial = backMaterial
                    };
                    
                    modelGroup.Children.Add(geometryModel);
                }
            }

            if (obj.Child != null)
            {
                ProcessObject(obj.Child, modelGroup, position);
            }

            if (obj.Sibling != null)
            {
                ProcessObject(obj.Sibling, modelGroup, parentPosition);
            }
        }

        private static MeshGeometry3D? ConvertObjectToMesh(ThreeDOObject obj, Point3D offset)
        {
            if (obj.Vertices == null || obj.Primitives == null)
                return null;

            var mesh = new MeshGeometry3D();

            var vertices = new Point3D[obj.Vertices.Length];
            for (int i = 0; i < obj.Vertices.Length; i++)
            {
                var v = obj.Vertices[i];
                vertices[i] = new Point3D(
                    offset.X + v.X / FixedPointScale,
                    offset.Y + v.Y / FixedPointScale,
                    offset.Z + v.Z / FixedPointScale
                );
                mesh.Positions.Add(vertices[i]);
            }

            foreach (var primitive in obj.Primitives)
            {
                if (primitive.VertexIndices == null || primitive.VertexIndices.Length < 3)
                    continue; // Skip points and lines

                if (primitive.Type == PrimitiveType.Triangle && primitive.VertexIndices.Length == 3)
                {
                    mesh.TriangleIndices.Add(primitive.VertexIndices[0]);
                    mesh.TriangleIndices.Add(primitive.VertexIndices[1]);
                    mesh.TriangleIndices.Add(primitive.VertexIndices[2]);
                }
                else if (primitive.Type == PrimitiveType.Quad && primitive.VertexIndices.Length == 4)
                {
                    // Split quad into two triangles
                    // Triangle 1: 0, 1, 2
                    mesh.TriangleIndices.Add(primitive.VertexIndices[0]);
                    mesh.TriangleIndices.Add(primitive.VertexIndices[1]);
                    mesh.TriangleIndices.Add(primitive.VertexIndices[2]);

                    // Triangle 2: 0, 2, 3
                    mesh.TriangleIndices.Add(primitive.VertexIndices[0]);
                    mesh.TriangleIndices.Add(primitive.VertexIndices[2]);
                    mesh.TriangleIndices.Add(primitive.VertexIndices[3]);
                }
            }

            if (mesh.TriangleIndices.Count > 0)
            {
                CalculateNormals(mesh);
            }

            return mesh.TriangleIndices.Count > 0 ? mesh : null;
        }

        private static void CalculateNormals(MeshGeometry3D mesh)
        {
            var normals = new Vector3D[mesh.Positions.Count];
            for (int i = 0; i < normals.Length; i++)
            {
                normals[i] = new Vector3D(0, 0, 0);
            }

            // Calculate face normals and accumulate
            for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
            {
                int i0 = mesh.TriangleIndices[i];
                int i1 = mesh.TriangleIndices[i + 1];
                int i2 = mesh.TriangleIndices[i + 2];

                var p0 = mesh.Positions[i0];
                var p1 = mesh.Positions[i1];
                var p2 = mesh.Positions[i2];

                var edge1 = new Vector3D(p1.X - p0.X, p1.Y - p0.Y, p1.Z - p0.Z);
                var edge2 = new Vector3D(p2.X - p0.X, p2.Y - p0.Y, p2.Z - p0.Z);

                var normal = Vector3D.CrossProduct(edge1, edge2);

                normals[i0] += normal;
                normals[i1] += normal;
                normals[i2] += normal;
            }

            mesh.Normals.Clear();
            for (int i = 0; i < normals.Length; i++)
            {
                normals[i].Normalize();
                mesh.Normals.Add(normals[i]);
            }
        }

        public static string GetModelInfo(ThreeDOFile threeDOFile)
        {
            if (threeDOFile?.RootObject == null)
                return "No model data";

            var totalVertices = threeDOFile.GetTotalVertexCount();
            var totalPrimitives = threeDOFile.GetTotalPrimitiveCount();
            var allObjects = threeDOFile.GetAllObjects();

            return $"3DO Model: {threeDOFile.RootObject.Name}\n" +
                   $"Objects: {allObjects.Count}\n" +
                   $"Total Vertices: {totalVertices}\n" +
                   $"Total Primitives: {totalPrimitives}";
        }
    }
}