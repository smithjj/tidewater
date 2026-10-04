using System;
using UnityEditor;
using UnityEngine;
using Tidewater.Sky;

// Bakes the night sky's fixed part (Runtime/Sky/StarField.cs: the stars, the Milky Way, the moonlit sky's glow) into the cubemap the physically based sky shows
// as its space emission: Assets/Tidewater/Resources/sky/Stars.bytes, half-float RGB in scene units (the JS radiance) compressed to BC6H, which
// DayNight turns back into a Cubemap when it starts (a Cubemap asset would be stored as hex text: twice the size).
//   Tidewater > Bake star cubemap
namespace Tidewater.EditorTools
{
	public static class StarBake
	{
		public const string AssetPath = "Assets/Tidewater/Resources/sky/Stars.bytes";
		public static int size = 1024;
		// row 0 of SetPixels is the top row of the OpenGL convention (checked with markers: flipping puts the horizon stars below it)
		public static bool flipY = false;

		// OpenGL cube map convention: face (+X, -X, +Y, -Y, +Z, -Z), sc / tc in [-1, 1] -> direction (Unity world)
		static Vector3 FaceDir( int face, float sc, float tc )
		{
			switch ( face )
			{
				case 0: return new Vector3( 1, - tc, - sc );
				case 1: return new Vector3( - 1, - tc, sc );
				case 2: return new Vector3( sc, 1, tc );
				case 3: return new Vector3( sc, - 1, - tc );
				case 4: return new Vector3( sc, - tc, 1 );
				default: return new Vector3( - sc, - tc, - 1 );
			}
		}

		// the face a direction falls on and its sc / tc there (the inverse of FaceDir)
		static int ProjectDir( Vector3 d, int forceFace, out float sc, out float tc )
		{
			int face = forceFace;
			if ( face < 0 )
			{
				float ax = Mathf.Abs( d.x ), ay = Mathf.Abs( d.y ), az = Mathf.Abs( d.z );
				face = ax >= ay && ax >= az ? ( d.x > 0 ? 0 : 1 ) : ay >= az ? ( d.y > 0 ? 2 : 3 ) : ( d.z > 0 ? 4 : 5 );
			}

			float ma;
			switch ( face )
			{
				case 0: ma = d.x; sc = - d.z; tc = - d.y; break;
				case 1: ma = - d.x; sc = d.z; tc = - d.y; break;
				case 2: ma = d.y; sc = d.x; tc = d.z; break;
				case 3: ma = - d.y; sc = d.x; tc = - d.z; break;
				case 4: ma = d.z; sc = d.x; tc = - d.y; break;
				default: ma = - d.z; sc = - d.x; tc = - d.y; break;
			}

			sc /= ma; tc /= ma;
			return face;
		}

		static int Row( int y, int n ) => flipY ? n - 1 - y : y;

		[MenuItem( "Tidewater/Bake star cubemap" )]
		public static void Bake() => Bake( size, null );

		// markers: optional debug directions (Unity world) painted as bright blobs, to check the orientation of the faces
		public static string Bake( int n, Vector3[] markers )
		{
			var t0 = DateTime.Now;
			int count; var faces = Compute( n, markers, out count );
			return Finish( faces, n, count, t0 );
		}

		// the part that does not touch Unity objects, so it can run off the main thread (the CLI gives the main thread 5 s per command):
		//   StartAsync( n, markers ) ... Status() ... Finish()
		static System.Threading.Tasks.Task<(Color[][], int)> task;
		static int taskSize; static DateTime taskStart;
		public static string StartAsync( int n, Vector3[] markers = null )
		{
			taskSize = n; taskStart = DateTime.Now;
			task = System.Threading.Tasks.Task.Run( () => { int c; var f = Compute( n, markers, out c ); return ( f, c ); } );
			return "started";
		}

		public static string Status() => task == null ? "idle" : task.IsCompleted ? ( task.IsFaulted ? "FAILED " + task.Exception : "done" ) : "running";

		public static string Finish()
		{
			if ( task == null || ! task.IsCompletedSuccessfully ) return "not done: " + Status();
			var ( faces, count ) = task.Result;
			task = null;
			return Finish( faces, taskSize, count, taskStart );
		}

		static Color[][] Compute( int n, Vector3[] markers, out int count )
		{
			var faces = new Color[ 6 ][];
			for ( int f = 0; f < 6; f ++ ) faces[ f ] = new Color[ n * n ];

			// the diffuse part, per texel (sim axes: the JS directions; Unity world is ( x, y, -z ))
			for ( int f = 0; f < 6; f ++ )
			for ( int y = 0; y < n; y ++ )
			for ( int x = 0; x < n; x ++ )
			{
				var d = FaceDir( f, ( x + 0.5f ) / n * 2 - 1, ( ( flipY ? n - 1 - y : y ) + 0.5f ) / n * 2 - 1 ).normalized;
				var g = StarField.Glow( new Vector3( d.x, d.y, - d.z ) );
				faces[ f ][ y * n + x ] = new Color( g.x, g.y, g.z, 1 );
			}

			// the stars, splatted (see StarField)
			float texel = Mathf.PI / 2 / n; // the angle of a texel at the centre of a face
			int cnt = 0;
			StarField.ForEachStar( s =>
			{
				cnt ++;
				var u = new Vector3( s.dir.x, s.dir.y, - s.dir.z );
				Splat( faces, n, u, s.color, StarField.STAR_SIGMA * s.size / StarField.STAR_CELLS, texel );
			} );
			if ( markers != null )
				foreach ( var m in markers ) Splat( faces, n, m.normalized, new Vector3( 300, 300, 300 ), 6f * texel, texel, 6f );

			count = cnt;
			return faces;
		}

		static string Finish( Color[][] faces, int n, int count, DateTime t0 )
		{
			var cube = new Cubemap( n, TextureFormat.RGBAHalf, false ) { name = "Stars" };
			for ( int f = 0; f < 6; f ++ ) cube.SetPixels( faces[ f ], ( CubemapFace ) f );
			cube.Apply( false, false );
			EditorUtility.CompressCubemapTexture( cube, TextureFormat.BC6H, TextureCompressionQuality.Normal );
			// file: "TWSTARS1", n, then the six BC6H faces (+X, -X, +Y, -Y, +Z, -Z), no mips
			using ( var ms = new System.IO.MemoryStream() )
			{
				ms.Write( System.Text.Encoding.ASCII.GetBytes( "TWSTARS1" ), 0, 8 );
				ms.Write( BitConverter.GetBytes( n ), 0, 4 );
				for ( int f = 0; f < 6; f ++ ) { var d = cube.GetPixelData<byte>( 0, ( CubemapFace ) f ); var a = d.ToArray(); ms.Write( a, 0, a.Length ); }
				System.IO.Directory.CreateDirectory( System.IO.Path.GetDirectoryName( AssetPath ) );
				System.IO.File.WriteAllBytes( AssetPath, ms.ToArray() );
			}

			UnityEngine.Object.DestroyImmediate( cube );
			AssetDatabase.ImportAsset( AssetPath );
			return $"baked {count} stars into a {n}px cubemap in {( DateTime.Now - t0 ).TotalSeconds:F1} s";
		}

		// add a Gaussian star at unit direction u: energy as the JS star of angular width sigma0, but never narrower than MIN texels
		static void Splat( Color[][] faces, int n, Vector3 u, Vector3 color, float sigma0, float texel, float minTexels = 0.65f )
		{
			float sigma = Mathf.Max( sigma0, minTexels * texel );
			float k = ( sigma0 * sigma0 ) / ( sigma * sigma );
			for ( int f = 0; f < 6; f ++ )
			{
				// only the faces the star is clearly on
				float sc0, tc0;
				ProjectDir( u, f, out sc0, out tc0 );
				var fd = FaceDir( f, 0, 0 );
				if ( Vector3.Dot( u, fd ) < 0.5f ) continue;
				float fx = ( sc0 + 1 ) * 0.5f * n - 0.5f, fy = ( tc0 + 1 ) * 0.5f * n - 0.5f; // texel coordinates (OpenGL row order)
				int r = Mathf.CeilToInt( 4f * sigma / texel ) + 2;
				for ( int dy = - r; dy <= r; dy ++ )
				for ( int dx = - r; dx <= r; dx ++ )
				{
					int x = Mathf.RoundToInt( fx ) + dx, yGl = Mathf.RoundToInt( fy ) + dy;
					if ( x < 0 || yGl < 0 || x >= n || yGl >= n ) continue;
					var d = FaceDir( f, ( x + 0.5f ) / n * 2 - 1, ( yGl + 0.5f ) / n * 2 - 1 ).normalized;
					float cos = Mathf.Clamp( Vector3.Dot( d, u ), -1f, 1f );
					float ang2 = 2f * ( 1f - cos ); // chord^2 ~ angle^2
					float w = k * Mathf.Exp( - ang2 / ( 2f * sigma * sigma ) );
					if ( w < 1e-6f ) continue;
					int y = flipY ? n - 1 - yGl : yGl;
					var c = faces[ f ][ y * n + x ];
					faces[ f ][ y * n + x ] = new Color( c.r + color.x * w, c.g + color.y * w, c.b + color.z * w, 1 );
				}
			}
		}
	}
}
