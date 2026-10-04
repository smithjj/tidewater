using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.Player;
using Tidewater.Util;
using Tidewater.World.Boat;
using UnityEngine;
using static Tidewater.World.Boat.GK;
using Matrix4 = Tidewater.Engine.Matrix4;
using Quaternion = Tidewater.Engine.Quaternion;
using Vector3 = Tidewater.Engine.Vector3;

// The view half of src/game/FishingRod.js: the rod and reel geometry (buildRodGeometry), the bobber (buildBobberGeometry) and the line, drawn from the
// state FishingRod.cs computes.
//
// The rod is one merged mesh whose moving parts are tagged in aux.w (1 rotor, 2 bail, 3 crank, 4 spool and drag knob, 5 braid on the spool) and
// animated, with the bend of the blank, in the Boat shader (kind 8: BoatFragment.hlsl ApplyMeshModification), driven by the _RodBend / _RodShape /
// _ReelAnim / _ReelAnim2 the rod's state writes here. The pattern coordinates are the rest position, kept in uv0 = ( x, y ) and uv3 = ( z, 0 ) so the
// weave stays on a part the vertex stage bends.
//
// The line is a camera-facing ribbon along a quadratic Bezier (tip -> sag -> bobber). The JS builds it in the vertex shader from two uniforms;
// here the 49 x 2 vertices are written on the CPU each frame (one small mesh, no draw call more than the JS has).
//
// Handedness: like every Unity view mesh, the rod and the bobber mirror x and are placed with Sim.BoatToUnity; the line is built in sim coordinates
// and converted vertex by vertex.
namespace Tidewater.Game
{
	public sealed class FishingRodView
	{
		const double ROD_L = FishingRod.ROD_L, BLANK_START = FishingRod.BLANK_START, REEL_Z = FishingRod.REEL_Z, BODY_Y = FishingRod.BODY_Y, PIVOT_Y = FishingRod.PIVOT_Y;

		// guides: position along the rod, ring inner radius, ring centre height below the blank's axis
		static readonly double[][] GUIDES =
		{
			new[] { 0.86, 0.0125, 0.068 }, new[] { 1.1, 0.0085, 0.046 }, new[] { 1.31, 0.0058, 0.031 }, new[] { 1.49, 0.0045, 0.023 },
			new[] { 1.65, 0.004, 0.0195 }, new[] { 1.8, 0.0036, 0.0165 }, new[] { 1.93, 0.0034, 0.0145 }, new[] { 2.035, 0.0032, 0.0125 },
		};

		static double blankR( double y ) => MathUtils.lerp( 0.0074, 0.0011, Math.Pow( MathUtils.clamp( ( y - BLANK_START ) / ( ROD_L - BLANK_START ), 0, 1 ), 0.85 ) );

		// GameMaterials.js PAT
		const double P_PLAIN = 0, P_CARBON = 8, P_THREAD = 9, P_EVA = 10, P_BRAID = 11, P_MACHINED = 12, P_KNURL = 13, P_RUBBER = 14;

		static Opts Mat( double color, double rough, double metal, double pattern ) => new Opts { color = color, rough = rough, metal = metal, pattern = pattern };
		// the JS `{ ...base, matrix, anim, color }`
		static Opts O( Opts b, Matrix4 matrix = null, double? anim = null, double? color = null )
		{
			var o = b.with( color: color, anim: anim );
			o.matrix = matrix;
			return o;
		}

		static Matrix4 M( double x, double y, double z, double rx = 0, double ry = 0, double rz = 0, double sx = 1, double? sy = null, double? sz = null ) => mat4( x, y, z, rx, ry, rz, sx, sy ?? sx, sz ?? sx );
		static Vector3 V( double x, double y, double z ) => new Vector3( x, y, z );
		static double[] pr( double r, double y ) => new[] { r, y };

		// ---------------------------------------------------------------- geometry

		// A 7 ft medium saltwater spinning combo at real scale: a tapered carbon blank under clear coat, split EVA grip, screw-down reel seat with hoods,
		// eight graduated guides with ceramic inserts and thread wraps, a hook keeper and tip-top; and a 3000-size spinning reel hanging under the seat
		// (gearbox body, rotor with a bail arm and line roller, spool wound with braid, front drag knob, crank handle with a T-knob).
		public static BufferGeometry BuildRodGeometry()
		{
			double L = ROD_L;
			var parts = new List<BufferGeometry>();
			Action<BufferGeometry, Opts> add = ( g, o ) => parts.Add( prepare( g, o ) );
			// materials ( colour, roughness, metalness, pattern )
			var BLANK = Mat( 0x15191e, 0.42, 0.15, P_CARBON );
			var WRAP = Mat( 0x0c0d10, 0.35, 0, P_THREAD );
			var TRIM = Mat( 0x8a6a2a, 0.3, 0.4, P_THREAD ); // gold metallic trim thread
			var EVA = Mat( 0x2a2b2d, 0.82, 0, P_EVA );
			var RUBBER = Mat( 0x151515, 0.8, 0, P_RUBBER );
			var GUN = Mat( 0x2d3034, 0.32, 0.85, P_MACHINED ); // gunmetal
			var SEAT = Mat( 0x1c1e21, 0.45, 0.2, P_PLAIN ); // graphite reel seat
			var KNURL = Mat( 0x3a3d42, 0.35, 0.9, P_KNURL );
			var FRAME = Mat( 0x2a2c2f, 0.25, 1, P_MACHINED ); // guide frames
			var INSERT = Mat( 0x55595e, 0.12, 0.3, P_PLAIN ); // SiC insert
			var STAINLESS = Mat( 0xb7bcc0, 0.16, 1, P_MACHINED );
			var CHAMP = Mat( 0xa88d5a, 0.24, 1, P_MACHINED ); // champagne anodised
			var BLACK = Mat( 0x111214, 0.4, 0.3, P_PLAIN );
			var BRAID = Mat( 0x7f9a5a, 0.7, 0, P_BRAID ); // moss-green braid

			// ---------------------------------------------------------------- rod
			// butt cap
			add( lathe( new[] { pr( 0, 0 ), pr( 0.012, 0.001 ), pr( 0.0152, 0.006 ), pr( 0.0156, 0.016 ), pr( 0.0149, 0.022 ) }, 24 ), RUBBER );
			// rear grip (EVA), swelling a little in the middle
			add( lathe( new[] { pr( 0.0147, 0.022 ), pr( 0.0151, 0.07 ), pr( 0.0152, 0.13 ), pr( 0.0144, 0.2 ), pr( 0.0131, 0.248 ) }, 24 ), EVA );
			// winding check, a short bare blank (split grip) with a trim ring
			add( lathe( new[] { pr( 0.0128, 0.248 ), pr( 0.0128, 0.252 ), pr( 0.0098, 0.256 ) }, 20 ), CHAMP );
			add( cylinder( 0.0086, 0.009, 0.074, 16 ), O( BLANK, M( 0, 0.293, 0 ) ) );
			add( cylinder( 0.0093, 0.0093, 0.006, 16 ), O( TRIM, M( 0, 0.305, 0 ) ) );
			// reel seat: knurled lock nut, threads, rear hood, barrel (foot on the -Z side), front hood
			add( cylinder( 0.0128, 0.0128, 0.024, 24 ), O( KNURL, M( 0, 0.342, 0 ) ) );
			add( lathe( new[] { pr( 0.0105, 0.354 ), pr( 0.0118, 0.358 ), pr( 0.0121, 0.372 ), pr( 0.0112, 0.38 ) }, 20 ), GUN );
			add( cylinder( 0.0104, 0.0104, 0.056, 20 ), O( SEAT, M( 0, 0.405, 0 ) ) );
			add( lathe( new[] { pr( 0.0112, 0.43 ), pr( 0.0121, 0.438 ), pr( 0.0118, 0.448 ), pr( 0.0102, 0.452 ) }, 20 ), GUN );
			// fore grip (EVA) and winding check
			add( lathe( new[] { pr( 0.0112, 0.452 ), pr( 0.0118, 0.462 ), pr( 0.0112, 0.5 ), pr( 0.0095, 0.53 ) }, 20 ), EVA );
			add( lathe( new[] { pr( 0.0095, 0.53 ), pr( 0.0095, 0.533 ), pr( 0.0078, 0.536 ) }, 16 ), CHAMP );
			// the blank: tapered, many rings so it bends smoothly
			{
				var prof = new List<double[]>();
				int n = 70;
				for ( int i = 0; i <= n; i ++ )
				{
					double y = MathUtils.lerp( BLANK_START, L - 0.004, ( double ) i / n );
					prof.Add( pr( blankR( y ), y ) );
				}

				add( lathe( prof.ToArray(), 12 ), BLANK );
			}

			// decal band and trim ahead of the fore grip
			add( cylinder( blankR( 0.6 ) + 0.0004, blankR( 0.56 ) + 0.0004, 0.05, 12 ), O( WRAP, M( 0, 0.585, 0 ) ) );
			add( cylinder( blankR( 0.61 ) + 0.0006, blankR( 0.61 ) + 0.0006, 0.003, 12 ), O( TRIM, M( 0, 0.612, 0 ) ) );
			// hook keeper: a small wire loop under the blank
			add( torus( 0.0035, 0.0006, 5, 14, Math.PI ), O( STAINLESS, M( 0, 0.572, - blankR( 0.572 ) - 0.0005, 0, Math.PI / 2, Math.PI / 2 ) ) );
			add( cylinder( blankR( 0.572 ) + 0.0005, blankR( 0.572 ) + 0.0005, 0.012, 10 ), O( WRAP, M( 0, 0.572, 0 ) ) );

			// guides: frame ring with a ceramic insert, two legs to a foot on the blank, thread wraps
			for ( int i = 0; i < GUIDES.Length; i ++ )
			{
				double y = GUIDES[ i ][ 0 ], r = GUIDES[ i ][ 1 ], hgt = GUIDES[ i ][ 2 ];
				double br = blankR( y );
				double zc = - hgt;
				double fr = r + Math.Max( 0.0011, r * 0.16 ); // frame ring radius
				double t = Math.Max( 0.0006, r * 0.07 );
				add( torus( fr, t * 1.3, 6, 22 ), O( FRAME, M( 0, y, zc, Math.PI / 2, 0, 0 ) ) );
				add( torus( r + t * 0.6, t * 0.9, 6, 22 ), O( INSERT, M( 0, y, zc, Math.PI / 2, 0, 0 ) ) );
				// legs splay from the foot to either side of the ring
				double footL = Math.Max( 0.012, hgt * 0.55 );
				double yf = y - footL * 0.7;
				foreach ( int sx in new[] { - 1, 1 } )
				{
					var top = V( sx * fr * 0.72, y - 0.0005, zc + fr * 0.69 );
					add( rod( V( 0, yf, - br - 0.0008 ), top, Math.Max( 0.0007, r * 0.09 ), 5, Math.Max( 0.0005, r * 0.07 ) ), FRAME );
				}

				// the foot on the blank and its wrap
				add( roundedBox( 0.0035, footL, 0.0012, 0.0005, 1 ), O( FRAME, M( 0, yf, - br - 0.0006 ) ) );
				add( cylinder( blankR( yf + footL * 0.6 ) + 0.0007, blankR( yf - footL * 0.6 ) + 0.0007, footL * 1.25, 10 ), O( WRAP, M( 0, yf, 0 ) ) );
				add( cylinder( blankR( yf + footL * 0.64 ) + 0.00085, blankR( yf + footL * 0.64 ) + 0.00085, 0.0016, 10 ), O( TRIM, M( 0, yf + footL * 0.64, 0 ) ) );
			}

			// tip-top: tube over the blank end and a small ring
			add( cylinder( 0.0014, 0.0016, 0.012, 8 ), O( FRAME, M( 0, L - 0.006, 0 ) ) );
			add( torus( 0.0029, 0.0007, 6, 16 ), O( FRAME, M( 0, L, - 0.004, Math.PI / 2, 0, 0 ) ) );
			add( torus( 0.0023, 0.0005, 6, 16 ), O( INSERT, M( 0, L, - 0.004, Math.PI / 2, 0, 0 ) ) );

			// the line from the spool through every guide to the tip (bends with the blank)
			{
				var pts = new List<Vector3> { V( 0, 0.425, REEL_Z + 0.022 ) };
				foreach ( var g in GUIDES ) pts.Add( V( 0, g[ 0 ], - g[ 2 ] + 0.0005 ) );
				pts.Add( V( 0, L, - 0.004 ) );
				var LINE = Mat( 0x9fb07e, 0.6, 0, P_PLAIN );
				for ( int i = 0; i < pts.Count - 1; i ++ ) add( rod( pts[ i ], pts[ i + 1 ], 0.00028, 3 ), LINE );
			}

			// ---------------------------------------------------------------- reel (axis along +Y at z = REEL_Z)
			double RZ = REEL_Z;
			// foot in the seat and the stem down to the body
			add( roundedBox( 0.011, 0.062, 0.004, 0.0015, 2 ), O( GUN, M( 0, 0.405, - 0.0118 ) ) );
			// the stem: one flat blade, wide along the rod, raked forward from the body to the foot
			add( roundedBox( 0.0072, 0.02, 0.08, 0.0032, 3 ), O( GUN, M( 0, 0.377, - 0.045, - 0.675, 0, 0 ) ) );
			add( roundedBox( 0.0082, 0.03, 0.012, 0.004, 3 ), O( GUN, M( 0, 0.352, RZ + 0.02, - 0.35, 0, 0 ) ) );
			// gearbox body: one smooth teardrop shell from the rotor neck back to the tail, flattened at the sides where the side plates sit
			add( lathe( new[] { pr( 0, 0.279 ), pr( 0.006, 0.28 ), pr( 0.0125, 0.286 ), pr( 0.0185, 0.298 ), pr( 0.0225, 0.314 ), pr( 0.0238, 0.33 ), pr( 0.0228, 0.342 ), pr( 0.0198, 0.35 ), pr( 0.018, 0.352 ) }, 32 ), O( GUN, M( 0, 0, RZ, 0, 0, 0, 0.82, 1, 1 ) ) );
			// side plates: slim inset discs with a machined trim ring (the handle side carries the crank boss)
			foreach ( int sx in new[] { - 1, 1 } )
			{
				add( cylinder( 0.0142, 0.0148, 0.0022, 28 ), O( BLACK, M( sx * 0.0188, BODY_Y, RZ, 0, 0, Math.PI / 2 ) ) );
				add( torus( 0.0145, 0.0007, 6, 32 ), O( CHAMP, M( sx * 0.0197, BODY_Y, RZ, 0, Math.PI / 2, 0 ) ) );
			}

			add( cylinder( 0.0068, 0.0085, 0.011, 18 ), O( GUN, M( - 0.025, BODY_Y, RZ, 0, 0, Math.PI / 2 ) ) );
			add( cylinder( 0.0045, 0.0045, 0.004, 12 ), O( CHAMP, M( 0.021, BODY_Y, RZ, 0, 0, Math.PI / 2 ) ) ); // screw cap
			// neck and trim ring between body and rotor
			add( lathe( new[] { pr( 0.018, 0.348 ), pr( 0.0205, 0.353 ), pr( 0.0208, 0.357 ) }, 28 ), O( CHAMP, M( 0, 0, RZ ) ) );

			// rotor (anim 1): a cup open to the front, two arms carrying the bail
			var ROT = O( BLACK, anim: 1 );
			add( lathe( new[] { pr( 0.006, 0.356 ), pr( 0.017, 0.358 ), pr( 0.0235, 0.362 ), pr( 0.0268, 0.37 ), pr( 0.0275, 0.381 ), pr( 0.0262, 0.386 ) }, 32 ), O( ROT, M( 0, 0, RZ ) ) );
			add( lathe( new[] { pr( 0.0262, 0.386 ), pr( 0.0248, 0.3865 ) }, 32 ), O( CHAMP, M( 0, 0, RZ ), anim: 1 ) );
			foreach ( int sx in new[] { - 1, 1 } )
			{
				add( roundedBox( 0.0065, 0.044, 0.013, 0.002, 2 ), O( ROT, M( sx * 0.0285, 0.382, RZ ) ) );
				// bail arm plates at the pivots (anim 2: they flip with the bail)
				add( roundedBox( 0.003, 0.012, 0.008, 0.001, 1 ), O( STAINLESS, M( sx * 0.0322, PIVOT_Y, RZ ), anim: 2 ) );
			}

			// bail wire (anim 2): around the spool from one arm to the other, and the line roller
			{
				var pts = new List<Vector3>();
				for ( int i = 0; i <= 16; i ++ )
				{
					double a = Math.PI * i / 16;
					pts.Add( V( 0.0322 * Math.Cos( a ), PIVOT_Y + 0.006 * Math.Sin( a ), RZ - 0.0322 * Math.Sin( a ) ) );
				}

				add( tube( pts, 0.0011, 40, 6 ), O( STAINLESS, anim: 2 ) );
				add( cylinder( 0.0032, 0.0032, 0.006, 12 ), O( CHAMP, M( 0.0302, PIVOT_Y + 0.0015, RZ - 0.0035, Math.PI / 2, 0, 0 ), anim: 2 ) );
			}

			// spool (anim 4) with braid (anim 5), front lip and drag knob
			Func<Opts, Opts> SP = o => O( o, M( 0, 0, RZ ), anim: 4 );
			add( lathe( new[] { pr( 0.0282, 0.375 ), pr( 0.0288, 0.381 ), pr( 0.0285, 0.388 ), pr( 0.0252, 0.392 ), pr( 0.0238, 0.393 ) }, 36 ), SP( CHAMP ) );
			add( lathe( new[] { pr( 0.0236, 0.3925 ), pr( 0.0238, 0.395 ), pr( 0.0238, 0.41 ), pr( 0.0236, 0.4125 ) }, 36 ), O( BRAID, M( 0, 0, RZ ), anim: 5 ) );
			add( lathe( new[] { pr( 0.0205, 0.3922 ), pr( 0.0205, 0.4128 ) }, 24 ), SP( CHAMP ) ); // arbor under the braid
			add( lathe( new[] { pr( 0.0236, 0.4125 ), pr( 0.0262, 0.4135 ), pr( 0.0266, 0.4155 ), pr( 0.0235, 0.418 ), pr( 0.016, 0.4195 ), pr( 0.012, 0.42 ) }, 36 ), SP( CHAMP ) );
			add( lathe( new[] { pr( 0.012, 0.42 ), pr( 0.0125, 0.422 ), pr( 0.0126, 0.431 ), pr( 0.0118, 0.434 ), pr( 0.009, 0.4355 ), pr( 0, 0.436 ) }, 24 ), SP( b( BLACK, P_KNURL ) ) );

			// crank handle (anim 3): shaft cap, arm, T-knob (on the left, cranked with the other hand)
			Func<Opts, Matrix4, Opts> CR = ( o, m ) => O( o, m, anim: 3 );
			add( cylinder( 0.0052, 0.0052, 0.008, 12 ), CR( GUN, M( - 0.0315, BODY_Y, RZ, 0, 0, Math.PI / 2 ) ) );
			add( roundedBox( 0.0038, 0.058, 0.0075, 0.0018, 2 ), CR( CHAMP, M( - 0.036, BODY_Y - 0.026, RZ ) ) );
			add( cylinder( 0.0026, 0.0026, 0.01, 8 ), CR( STAINLESS, M( - 0.042, BODY_Y - 0.052, RZ, 0, 0, Math.PI / 2 ) ) );
			add( lathe( new[] { pr( 0.003, 0 ), pr( 0.0062, 0.0015 ), pr( 0.0071, 0.007 ), pr( 0.0068, 0.018 ), pr( 0.0048, 0.0225 ), pr( 0, 0.0235 ) }, 16 ), CR( O( RUBBER, color: 0x1d1e20 ), M( - 0.046, BODY_Y - 0.052, RZ, 0, 0, Math.PI / 2 ) ) );

			return GeoKit.mergePrepared( parts );
		}

		// the JS `{ ...BLACK, pattern: PAT.knurl }`
		static Opts b( Opts o, double pattern ) => o.with( pattern: pattern );

		public static BufferGeometry BuildBobberGeometry()
		{
			var parts = new List<BufferGeometry>
			{
				prepare( sphere( 0.028, 16, 8, 0, Math.PI * 2, 0, Math.PI / 2 ), Mat( 0xc8261c, 0.35, 0, 0 ) ),
				prepare( sphere( 0.028, 16, 8, 0, Math.PI * 2, Math.PI / 2, Math.PI / 2 ), Mat( 0xeeeae0, 0.35, 0, 0 ) ),
				prepare( cylinder( 0.004, 0.004, 0.07, 6 ), O( Mat( 0xd9d4c8, 0.5, 0, 0 ), mat4( 0, 0.055, 0 ) ) ),
				prepare( cylinder( 0.0035, 0.0045, 0.03, 6 ), O( Mat( 0xffd400, 0.4, 0, 0 ), mat4( 0, 0.1, 0 ) ) ),
			};
			return GeoKit.mergePrepared( parts );
		}

		// the Unity mesh of a prop (mirrored in x, see UnityMesh) with the rest position of each vertex in uv0 = ( x, y ) and uv3 = ( z, 0 ), in the JS frame
		static UnityEngine.Mesh PropMesh( BufferGeometry g, string name )
		{
			var m = UnityMesh.Create( g, name );
			var pos = g.attributes[ "position" ];
			int n = pos.count;
			var uv0 = new List<UnityEngine.Vector2>( n ); var uv3 = new List<UnityEngine.Vector2>( n );
			for ( int i = 0; i < n; i ++ ) { uv0.Add( new UnityEngine.Vector2( pos.array[ i * 3 ], pos.array[ i * 3 + 1 ] ) ); uv3.Add( new UnityEngine.Vector2( pos.array[ i * 3 + 2 ], 0 ) ); }
			m.SetUVs( 0, uv0 );
			m.SetUVs( 3, uv3 );
			m.bounds = new Bounds( UnityEngine.Vector3.zero, new UnityEngine.Vector3( 6, 6, 6 ) ); // the blank bends and the camera is inside it
			return m;
		}

		// ---------------------------------------------------------------- the view

		const int SEGS = FishingRod.LINE_SEGS;

		readonly GameObject root, rodGo, bobberGo, lineGo;
		readonly UnityEngine.Material rodMat, bobberMat, lineMat;
		readonly UnityEngine.Mesh rodMesh, bobberMesh, lineMesh;
		readonly UnityEngine.Vector3[] linePos = new UnityEngine.Vector3[ ( SEGS + 1 ) * 2 ], lineNrm = new UnityEngine.Vector3[ ( SEGS + 1 ) * 2 ];
		static readonly Vector3 _p = new Vector3(), _t = new Vector3(), _c = new Vector3(), _w = new Vector3(), _scale = new Vector3();
		static readonly Quaternion _q = new Quaternion();

		FishingRodView( Transform parent )
		{
			// a leftover from a domain reload (the objects are not saved): drop it
			var old = parent != null ? parent.Find( "Fishing" ) : null;
			if ( old != null ) { DestroyAll( old.gameObject ); }
			root = new GameObject( "Fishing" ) { hideFlags = HideFlags.DontSave };
			if ( parent != null ) root.transform.SetParent( parent, false );

			var shader = Shader.Find( "Tidewater/Boat" );
			rodMat = Prop( shader, "rod" );
			bobberMat = Prop( shader, "bobber" );
			lineMat = new UnityEngine.Material( shader ) { name = "fishingLine", hideFlags = HideFlags.HideAndDontSave };
			var lc = linearColor( 0xd8dde0 );
			lineMat.SetFloat( "_BoatKind", 7 );
			lineMat.SetFloat( "_CullMode", 0f );
			lineMat.SetColor( "_FacColor", new UnityEngine.Color( ( float ) lc.r, ( float ) lc.g, ( float ) lc.b, 1f ) );
			lineMat.SetVector( "_FacPbr", new UnityEngine.Vector4( 0.35f, 0f, 0f, 0.1f ) );
			lineMat.SetColor( "_FacEmissive", UnityEngine.Color.black );

			rodMesh = PropMesh( BuildRodGeometry(), "FishingRod" );
			bobberMesh = PropMesh( BuildBobberGeometry(), "Bobber" );
			lineMesh = BuildLineMesh();
			rodGo = Make( "FishingRod", rodMesh, rodMat );
			bobberGo = Make( "Bobber", bobberMesh, bobberMat );
			lineGo = Make( "FishingLine", lineMesh, lineMat );
			lineGo.transform.SetParent( root.transform, false );
			rodGo.SetActive( false ); bobberGo.SetActive( false ); lineGo.SetActive( false );
		}

		public static FishingRodView Create( Transform parent ) => new FishingRodView( parent );

		// false once the Editor has destroyed the objects (entering Play mode drops what the Editor made, while the C# objects survive)
		public bool Alive => root != null;

		static UnityEngine.Material Prop( Shader shader, string name )
		{
			var m = new UnityEngine.Material( shader ) { name = name, hideFlags = HideFlags.HideAndDontSave };
			m.SetFloat( "_BoatKind", 8 );
			m.SetFloat( "_CullMode", 2f );
			return m;
		}

		GameObject Make( string name, UnityEngine.Mesh mesh, UnityEngine.Material mat )
		{
			var go = new GameObject( name ) { hideFlags = HideFlags.DontSave };
			go.transform.SetParent( root.transform, false );
			go.AddComponent<MeshFilter>().sharedMesh = mesh;
			var mr = go.AddComponent<MeshRenderer>();
			mr.sharedMaterial = mat;
			mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			return go;
		}

		static UnityEngine.Mesh BuildLineMesh()
		{
			var m = new UnityEngine.Mesh { name = "FishingLine", hideFlags = HideFlags.DontSave };
			m.MarkDynamic();
			var v = new UnityEngine.Vector3[ ( SEGS + 1 ) * 2 ];
			var idx = new List<int>();
			for ( int i = 0; i < SEGS; i ++ ) { int a = i * 2; idx.AddRange( new[] { a, a + 1, a + 2, a + 1, a + 3, a + 2 } ); }
			m.vertices = v;
			m.triangles = idx.ToArray();
			m.bounds = new Bounds( UnityEngine.Vector3.zero, new UnityEngine.Vector3( 4000, 4000, 4000 ) );
			return m;
		}

		static void DestroyAll( GameObject go )
		{
			foreach ( var mf in go.GetComponentsInChildren<MeshFilter>( true ) ) if ( mf.sharedMesh != null ) UnityEngine.Object.DestroyImmediate( mf.sharedMesh );
			UnityEngine.Object.DestroyImmediate( go );
		}

		public void Dispose()
		{
			if ( root != null ) DestroyAll( root );
			if ( rodMat != null ) UnityEngine.Object.DestroyImmediate( rodMat );
			if ( bobberMat != null ) UnityEngine.Object.DestroyImmediate( bobberMat );
			if ( lineMat != null ) UnityEngine.Object.DestroyImmediate( lineMat );
		}

		// the rod, the bobber and the line in the state of `rod` (call after FishingRod.update): FishingRod.js writes the same to its meshes and
		// uniforms at the end of update
		public void Sync( FishingRod rod, SimCamera camera )
		{
			if ( root == null ) return;
			// the rod
			rodGo.SetActive( rod.rodVisible );
			if ( rod.rodVisible )
			{
				rod.rodMatrix.decompose( _p, _q, _scale );
				rodGo.transform.SetPositionAndRotation( Sim.ToUnity( _p.x, _p.y, _p.z ), Sim.BoatToUnity( _q ) );
				rodMat.SetVector( "_RodBend", new UnityEngine.Vector4( ( float ) rod.rodBend[ 0 ], ( float ) rod.rodBend[ 1 ], ( float ) rod.rodBend[ 2 ], ( float ) rod.rodBend[ 3 ] ) );
				rodMat.SetVector( "_RodShape", new UnityEngine.Vector4( ( float ) rod.rodShapeX, 0, 0, 0 ) );
				rodMat.SetVector( "_ReelAnim", new UnityEngine.Vector4( ( float ) rod.reelAnim[ 0 ], ( float ) rod.reelAnim[ 1 ], ( float ) rod.reelAnim[ 2 ], ( float ) rod.reelAnim[ 3 ] ) );
				rodMat.SetVector( "_ReelAnim2", new UnityEngine.Vector4( ( float ) rod.reelAnim2[ 0 ], ( float ) rod.reelAnim2[ 1 ], ( float ) rod.reelAnim2[ 2 ], ( float ) rod.reelAnim2[ 3 ] ) );
			}

			// the bobber (a real float is a few pixels at casting range: FishingRod grows it with distance)
			bobberGo.SetActive( rod.bobberVisible );
			if ( rod.bobberVisible )
			{
				_q.setFromAxisAngle( _w.set( 1, 0, 0 ), rod.bobberTilt );
				bobberGo.transform.SetPositionAndRotation( Sim.ToUnity( rod.bobber.x, rod.bobber.y, rod.bobber.z ), Sim.BoatToUnity( _q ) );
				bobberGo.transform.localScale = UnityEngine.Vector3.one * ( float ) rod.bobberScale;
			}

			// the line
			lineGo.SetActive( rod.lineVisible );
			if ( rod.lineVisible ) UpdateLine( rod, camera );
		}

		// the JS vertex shader of createLineMaterial: a ribbon along the quadratic Bezier about a pixel wide at any distance, facing the camera
		void UpdateLine( FishingRod rod, SimCamera camera )
		{
			var a = rod.lineA; var b = rod.lineB; var c = rod.lineCtl;
			for ( int i = 0; i <= SEGS; i ++ )
			{
				double t = ( double ) i / SEGS, u = 1 - t;
				_p.set( u * u * a.x + 2 * u * t * c.x + t * t * b.x, u * u * a.y + 2 * u * t * c.y + t * t * b.y, u * u * a.z + 2 * u * t * c.z + t * t * b.z );
				_t.set( 2 * u * ( c.x - a.x ) + 2 * t * ( b.x - c.x ) + 1e-6, 2 * u * ( c.y - a.y ) + 2 * t * ( b.y - c.y ), 2 * u * ( c.z - a.z ) + 2 * t * ( b.z - c.z ) ).normalize();
				_c.set( camera.position.x - _p.x, camera.position.y - _p.y, camera.position.z - _p.z );
				double dist = _c.length();
				_w.crossVectors( _t, _c );
				_w.y += 1e-6;
				_w.normalize();
				// about a pixel wide at any distance (TAA resolves the sub-pixel coverage)
				double w = Math.Max( 0.0006, dist * 0.0006 ) * rod.lineShow;
				_c.normalize();
				for ( int s = 0; s < 2; s ++ )
				{
					double side = s == 1 ? 1 : - 1;
					linePos[ i * 2 + s ] = Sim.ToUnity( _p.x + _w.x * side * w, _p.y + _w.y * side * w, _p.z + _w.z * side * w );
					lineNrm[ i * 2 + s ] = Sim.ToUnity( _c.x, _c.y, _c.z );
				}
			}

			lineMesh.SetVertices( linePos );
			lineMesh.SetNormals( lineNrm );
		}
	}

}
