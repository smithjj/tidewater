using System;
using Tidewater.Engine;
using Tidewater.World;
using UnityEngine;
using Vector3 = Tidewater.Engine.Vector3;
using Color = UnityEngine.Color;
using Quaternion = UnityEngine.Quaternion;
using Mathf = UnityEngine.Mathf;

// Where the two traders keep shop (src/game/FishStand.js STAND, Chandlery.js CHANDLERY), their colliders, and the stand-in stalls and figures.
// The JS builds the stalls from photoscanned Poly Haven surfaces (StallKit) and puts a Rocketbox character behind the counter; neither is ported yet,
// so here a plank stall and a trestle table of plain boxes stand where they will be, with the same colliders and the same trader positions, and the
// figure is a few primitives (the JS stand-in figure's proportions).
namespace Tidewater.Game
{
	public static class Stalls
	{
		public static readonly double[] STAND = { 49.9, - 74.6, 1.45 };      // x, z, yaw: beside the boardwalk up from the pier foot, facing it
		public static readonly double[] CHANDLERY = { 85.5, - 60.5, - 1.9 }; // x, z, yaw: faces the beach and the pier
		const double STALL_FLOOR = 0.06; // top of the stall's plank floor (local y)

		// local (lx, lz) of a stall -> world (x, z): Vector3.applyAxisAngle( Y, yaw )
		public static void ToWorld( double[] s, double lx, double lz, out double x, out double z )
		{
			double c = Math.Cos( s[ 2 ] ), sn = Math.Sin( s[ 2 ] );
			x = s[ 0 ] + lx * c + lz * sn; z = s[ 1 ] - lx * sn + lz * c;
		}

		// the two vendors and their colliders; `heightAt` is the terrain
		public static Vendor[] Create( System.Func<double, double, double> heightAt, Colliders colliders )
		{
			double y = heightAt( STAND[ 0 ], STAND[ 1 ] );
			ToWorld( STAND, 0.2, 0.12, out double jx, out double jz );
			var joe = new Vendor( "Joe · Fish buyer", "buyer", new Vector3( jx, y + STALL_FLOOR, jz ), STAND[ 2 ], 3.2, new[] { 6.0, 19.0 },
				"Let's see what you caught. Fair prices, cash.", "Nothing to sell? The grunts are biting off the pier." );
			double cy = heightAt( CHANDLERY[ 0 ], CHANDLERY[ 1 ] );
			ToWorld( CHANDLERY, 0, - 0.75, out double mx, out double mz );
			var marta = new Vendor( "Marta · Chandlery", "shop", new Vector3( mx, heightAt( mx, mz ), mz ), CHANDLERY[ 2 ], 3.0, new[] { 7.0, 18.0 },
				"Line, reels, a bigger hold, diesel. What do you need?" );
			if ( colliders != null )
			{
				colliders.addBox( new Vector3( STAND[ 0 ], y + 1.2, STAND[ 1 ] ), new Vector3( 1.45, 1.2, 0.95 ), STAND[ 2 ], false, true, "fishStand" );
				// the crates, the bucket and the chalkboard around it
				foreach ( var b in new[] { new[] { - 1.85, 0.25, 0.45, 0.25, 0.35 }, new[] { 1.8, - 0.2, 0.3, 0.6, 0.23 }, new[] { 1.55, 1.05, 0.2, 0.2, 0.28 }, new[] { 1.95, 1.45, 0.35, 0.25, 0.42 } } )
				{
					ToWorld( STAND, b[ 0 ], b[ 1 ], out double wx, out double wz );
					colliders.addBox( new Vector3( wx, y + b[ 4 ], wz ), new Vector3( b[ 2 ], b[ 4 ], b[ 3 ] ), STAND[ 2 ], false, true, "fishStandProps" );
				}

				colliders.addBox( new Vector3( CHANDLERY[ 0 ], cy + 0.45, CHANDLERY[ 1 ] ), new Vector3( 1.0, 0.45, 0.4 ), CHANDLERY[ 2 ], false, true, "chandlery" );
				// shelves and sign posts behind her, the jerrycans and the rod rack
				foreach ( var b in new[] { new[] { 0, - 1.4, 0.62, 0.22, 0.8 }, new[] { 1.25, 0.2, 0.3, 0.3, 0.26 }, new[] { - 1.35, - 0.9, 0.15, 0.3, 0.9 } } )
				{
					ToWorld( CHANDLERY, b[ 0 ], b[ 1 ], out double wx, out double wz );
					colliders.addBox( new Vector3( wx, cy + b[ 4 ], wz ), new Vector3( b[ 2 ], b[ 4 ], b[ 3 ] ), CHANDLERY[ 2 ], false, true, "chandleryProps" );
				}
			}

			return new[] { joe, marta };
		}
	}

	// the stand-in stalls and figures as Unity objects (built at run time, in the stall's sim-local frame mirrored into Unity: local (x, y, z) -> (x, y, -z))
	public sealed class StallsView : MonoBehaviour
	{
		Transform figureJoe, figureMarta;
		Vendor joe, marta;
		readonly System.Collections.Generic.Dictionary<uint, Material> mats = new System.Collections.Generic.Dictionary<uint, Material>();

		Material Mat( Color c, float smooth = 0.25f )
		{
			uint key = ( uint ) ( ( ( int ) ( c.r * 255 ) << 16 ) | ( ( int ) ( c.g * 255 ) << 8 ) | ( int ) ( c.b * 255 ) ) | ( ( uint ) ( smooth * 100 ) << 24 );
			if ( mats.TryGetValue( key, out var m ) ) return m;
			m = new Material( Shader.Find( "HDRP/Lit" ) ) { name = "stall", hideFlags = HideFlags.HideAndDontSave };
			m.SetColor( "_BaseColor", c ); m.SetFloat( "_Smoothness", smooth ); m.SetFloat( "_Metallic", 0 );
			mats[ key ] = m;
			return m;
		}

		static Color Hex( int h ) => new Color( ( ( h >> 16 ) & 255 ) / 255f, ( ( h >> 8 ) & 255 ) / 255f, ( h & 255 ) / 255f );

		// a box centred at the sim-local (x, y, z), size (sx, sy, sz), turned by rx about x (sim) and ry about y
		GameObject Box( Transform parent, string n, double x, double y, double z, double sx, double sy, double sz, int hex, double ry = 0, double rx = 0, float smooth = 0.2f )
			=> Prim( PrimitiveType.Cube, parent, n, x, y, z, sx, sy, sz, hex, ry, rx, smooth );

		GameObject Prim( PrimitiveType t, Transform parent, string n, double x, double y, double z, double sx, double sy, double sz, int hex, double ry = 0, double rx = 0, float smooth = 0.2f )
		{
			var g = GameObject.CreatePrimitive( t );
			g.name = n;
			DestroyImmediate( g.GetComponent<Collider>() );
			g.transform.SetParent( parent, false );
			g.transform.localPosition = new UnityEngine.Vector3( ( float ) x, ( float ) y, ( float ) - z );
			g.transform.localRotation = Quaternion.Euler( ( float ) ( rx * Mathf.Rad2Deg ), ( float ) - ( ry * Mathf.Rad2Deg ), 0 );
			g.transform.localScale = new UnityEngine.Vector3( ( float ) sx, ( float ) sy, ( float ) sz );
			g.GetComponent<MeshRenderer>().sharedMaterial = Mat( Hex( hex ), smooth );
			return g;
		}

		// a rod between two sim-local points
		void Rod( Transform parent, string n, double[] a, double[] b, double r, int hex )
		{
			var pa = new UnityEngine.Vector3( ( float ) a[ 0 ], ( float ) a[ 1 ], ( float ) - a[ 2 ] ); var pb = new UnityEngine.Vector3( ( float ) b[ 0 ], ( float ) b[ 1 ], ( float ) - b[ 2 ] );
			var g = GameObject.CreatePrimitive( PrimitiveType.Cylinder ); g.name = n; DestroyImmediate( g.GetComponent<Collider>() );
			g.transform.SetParent( parent, false );
			g.transform.localPosition = ( pa + pb ) / 2;
			g.transform.localRotation = Quaternion.FromToRotation( UnityEngine.Vector3.up, pb - pa );
			g.transform.localScale = new UnityEngine.Vector3( ( float ) ( 2 * r ), ( pb - pa ).magnitude / 2, ( float ) ( 2 * r ) );
			g.GetComponent<MeshRenderer>().sharedMaterial = Mat( Hex( hex ), 0.4f );
		}

		Transform Group( string name, double[] s, double y )
		{
			var g = new GameObject( name ).transform;
			g.SetParent( transform, false );
			g.position = new UnityEngine.Vector3( ( float ) s[ 0 ], ( float ) y, ( float ) - s[ 1 ] );
			g.rotation = Quaternion.Euler( 0, ( float ) - ( s[ 2 ] * Mathf.Rad2Deg ), 0 );
			return g;
		}

		// ---- the fish buyer's plank stall: floor, back and side walls, a counter, a tin roof, an ice chest on the counter (local: counter toward +z)
		void BuildStand( System.Func<double, double, double> heightAt )
		{
			var g = Group( "FishStand", Stalls.STAND, heightAt( Stalls.STAND[ 0 ], Stalls.STAND[ 1 ] ) );
			Box( g, "floor", 0, 0.03, 0, 2.9, 0.06, 1.9, 0x7d6d5a );
			Box( g, "back", 0, 1.15, - 0.9, 2.9, 2.2, 0.08, 0x8e7e68 );
			foreach ( double s in new[] { - 1, 1 } ) Box( g, "side", s * 1.41, 1.15, - 0.15, 0.08, 2.2, 1.6, 0x8e7e68 );
			Box( g, "counter", 0, 0.5, 0.62, 2.7, 1.0, 0.4, 0x958670 );
			Box( g, "counterTop", 0, 1.02, 0.62, 2.8, 0.05, 0.5, 0xa89a82 );
			Box( g, "roof", 0, 2.4, - 0.05, 3.3, 0.05, 2.3, 0x8a5a3c, 0, - 0.08, 0.35f );
			Box( g, "iceChest", 0.55, 1.15, 0.62, 0.9, 0.26, 0.42, 0xb7d3dc, 0, 0, 0.7f );
			Box( g, "crate", - 1.85, 0.35, 0.25, 0.9, 0.7, 0.5, 0x7a6b58, 0.1 );
			Box( g, "crate", 1.8, 0.23, - 0.2, 0.6, 0.46, 1.2, 0x7a6b58 );
			Box( g, "board", 1.95, 0.5, 1.45, 0.7, 0.84, 0.05, 0x2a302c, 0.2, - 0.1 );
		}

		// ---- the chandlery's trestle table with the tackle box, spools, jerrycans, a coil of rope and the floats (local: customers at +z, her behind at -0.75)
		void BuildChandlery( System.Func<double, double, double> heightAt )
		{
			var g = Group( "Chandlery", Stalls.CHANDLERY, heightAt( Stalls.CHANDLERY[ 0 ], Stalls.CHANDLERY[ 1 ] ) );
			foreach ( double x in new[] { - 0.75, 0.75 } ) foreach ( double s in new[] { - 1, 1 } ) Box( g, "leg", x, 0.43, s * 0.22, 0.06, 0.95, 0.06, 0x7a6b58 );
			for ( int i = 0; i < 3; i ++ ) Box( g, "board", 0, 0.9, - 0.27 + i * 0.27, 2.0, 0.035, 0.26, new[] { 0x8e7e68, 0x7d6d5a, 0x958670 }[ i ] );
			Box( g, "tackleBox", - 0.55, 1.01, 0.02, 0.5, 0.18, 0.3, 0x2f6a4a );
			for ( int i = 0; i < 4; i ++ ) Prim( PrimitiveType.Cylinder, g.transform, "spool", 0.05 + i * 0.11, 0.945, 0.12, 0.09, 0.025, 0.09, new[] { 0xd8d4c8, 0x3aa0c8, 0xe0c040, 0xd8d4c8 }[ i ], 0, Mathf.PI / 2, 0.5f );
			for ( int i = 0; i < 3; i ++ ) Box( g, "jerrycan", 1.25, 0.17, - 0.2 + i * 0.22, 0.18, 0.34, 0.3, i == 1 ? 0x1f5a2a : 0xb2261c, 0.1 * i, 0, 0.55f );
			for ( int i = 0; i < 4; i ++ ) Prim( PrimitiveType.Cylinder, g.transform, "rope", - 1.3, 0.02 + i * 0.035, 0.2, 0.4 - i * 0.024, 0.017, 0.4 - i * 0.024, 0xc9b48a );
			for ( int i = 0; i < 3; i ++ ) Prim( PrimitiveType.Sphere, g.transform, "float", - 1.1 + i * 0.12, 0.09, - 0.3, 0.18, 0.18, 0.18, new[] { 0xe2552a, 0xe8e2d0, 0xf2c230 }[ i ], 0, 0, 0.5f );
			Box( g, "shelf", 0, 0.8, - 1.4, 1.24, 1.6, 0.44, 0x7a6b58 );
			Box( g, "post", - 1.35, 0.9, - 0.9, 0.12, 1.8, 0.12, 0x7a6b58 );
			Box( g, "sign", - 1.35, 1.6, - 0.9, 0.7, 0.4, 0.04, 0x2a302c, 0 );
			Box( g, "priceBoard", 0, 0.5, 0.33, 0.7, 0.5, 0.025, 0x2a302c, 0, - 0.2 );
		}

		// ---- the stand-in figure (~1.72 m, facing local +z): a weathered islander in a hat, apron and boots (Vendor.js buildFigure's proportions)
		Transform BuildFigure( Vendor v, System.Func<double, double, double> heightAt, int shirt, int trousers, int apron, int hat, int hair )
		{
			var root = new GameObject( "Vendor:" + v.name ).transform;
			root.SetParent( transform, false );
			root.position = new UnityEngine.Vector3( ( float ) v.position.x, ( float ) v.position.y, ( float ) - v.position.z );
			root.rotation = Quaternion.Euler( 0, ( float ) - ( v.yaw * Mathf.Rad2Deg ), 0 );
			var f = new GameObject( "figure" ).transform;
			f.SetParent( root, false );
			const int SKIN = 0x9a6a4a, BOOTS = 0xe8e2d0;
			foreach ( double s in new[] { - 1, 1 } )
			{
				Prim( PrimitiveType.Cylinder, f, "boot", s * 0.1, 0.18, 0.01, 0.15, 0.18, 0.15, BOOTS, 0, 0, 0.5f );
				Prim( PrimitiveType.Cylinder, f, "leg", s * 0.1, 0.6, 0, 0.17, 0.24, 0.17, trousers );
				Rod( f, "sleeve", new[] { s * 0.22, 1.3, 0 }, new[] { s * 0.25, 1.02, 0.05 }, 0.055, shirt );
				Rod( f, "forearm", new[] { s * 0.25, 1.02, 0.05 }, new[] { s * 0.2, 0.92, 0.27 }, 0.045, SKIN );
			}

			Box( f, "torso", 0, 1.1, - 0.01, 0.38, 0.52, 0.24, shirt );
			Box( f, "hips", 0, 0.86, 0, 0.36, 0.14, 0.22, trousers );
			Box( f, "apron", 0, 0.86, 0.125, 0.34, 0.62, 0.012, apron, 0, 0.05, 0.45f );
			Prim( PrimitiveType.Cylinder, f, "neck", 0, 1.41, 0.01, 0.11, 0.05, 0.11, SKIN );
			Prim( PrimitiveType.Sphere, f, "head", 0, 1.55, 0.02, 0.216, 0.242, 0.22, SKIN, 0, 0, 0.5f );
			Prim( PrimitiveType.Sphere, f, "hair", 0, 1.56, 0.0, 0.226, 0.2, 0.226, hair );
			Prim( PrimitiveType.Cylinder, f, "hatCrown", 0, 1.7, 0.01, 0.2, 0.05, 0.2, hat );
			Prim( PrimitiveType.Cylinder, f, "hatBrim", 0, 1.655, 0.01, 0.52, 0.0075, 0.52, hat, 0, - 0.05 );
			return f;
		}

		public static StallsView Build( Transform parent, System.Func<double, double, double> heightAt, Vendor joe, Vendor marta )
		{
			// the stalls are built at run time and never saved with the scene (a domain reload in the Editor leaves the old ones: clear them)
			for ( int i = parent.childCount - 1; i >= 0; i -- ) if ( parent.GetChild( i ).name == "Stalls" ) DestroyImmediate( parent.GetChild( i ).gameObject );
			var go = new GameObject( "Stalls" ) { hideFlags = HideFlags.DontSave };
			go.transform.SetParent( parent, false );
			var v = go.AddComponent<StallsView>();
			v.joe = joe; v.marta = marta;
			v.BuildStand( heightAt );
			v.BuildChandlery( heightAt );
			// Joe: a blue work shirt and an apron; Marta (Chandlery.js look): a red shirt, green apron, dark hat
			v.figureJoe = v.BuildFigure( joe, heightAt, 0x5d7a8c, 0x3f4a3c, 0xd8b24a, 0xc9a86a, 0xb8b2a6 );
			v.figureMarta = v.BuildFigure( marta, heightAt, 0x8a3b32, 0x2f3b4a, 0x3d5a4a, 0x2c3a44, 0x3a2c22 );
			foreach ( var t in go.GetComponentsInChildren<Transform>( true ) ) t.gameObject.hideFlags = HideFlags.DontSave;
			return v;
		}

		void Pose( Transform f, Vendor v )
		{
			if ( f == null || v == null ) return;
			f.localRotation = Quaternion.Euler( 0, ( float ) - ( v.figureYaw * Mathf.Rad2Deg ), ( float ) ( v.figureRoll * Mathf.Rad2Deg ) );
			f.localScale = new UnityEngine.Vector3( 1, ( float ) v.figureScaleY, 1 );
		}

		void LateUpdate() { Pose( figureJoe, joe ); Pose( figureMarta, marta ); }
	}
}
