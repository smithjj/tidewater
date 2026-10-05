using System;
using System.Collections.Generic;
using System.Linq;
using Tidewater.Engine;
using Tidewater.Util;

// Port of src/world/Fish.js (the simulation half): fish and other swimmers of the reef and the bay, simulated on the CPU.
//
// Habitats and behaviours:
//  - reef, by depth band: damselfish and wrasses on the shallow flats, grunts milling at coral heads, chromis hovering above them, parrotfish
//    and tangs foraging, angelfish pairs, yellowtail schools, groupers lurking by big heads, barracuda hanging in mid water;
//  - drop-off: bar jack patrols (they hunt the bait ball), an eagle ray flapping along the slope; a bait ball of silversides in mid water that
//    cruises as a polarized school and balls up, milling, when jacks or the swimmer come close (and parts around them);
//  - sand: southern stingrays gliding low (and resting), a green turtle that surfaces to breathe;
//  - bay: tarpon cruising in the deeper water, needlefish just under the surface, mullet schools near the shore (one now and then leaps out
//    with a splash), snappers and grunts around the pier piles, schools of fry in the shallows just outside the breakers.
// Everything scatters from the swimmer (also from someone wading in the shallows), stays in the water (terrain and reef height field,
// surface, out of the surf zone) and is only simulated near the camera.
//
// Sim coordinates (x east, y up, z south), everything in doubles; the per-fish state lives in float arrays like the JS Float32Arrays, so the
// stored values round the same way. The humpback's escort (pilot fish, juveniles, remoras: setWhale) is here; its WhaleWater is the ocean's. The motion vectors are not written by the view (the record keeps the
// motion fields). cull() is the one-camera pass of FishSchools.cull; FishSchoolsView builds the draw from its lists.
namespace Tidewater.World.Fish
{
	public sealed class FishZone
	{
		public double x, z, r;
		public double[] band;
		public List<double[]> path, anchors, piles;
		public bool sand;

		public FishZone Clone() => ( FishZone ) MemberwiseClone();
	}

	public sealed class FishGroup
	{
		public Behaviour sp;
		public int count, offset; // offset: first fish index
		public FishZone zone;
		public Vector3 home, goal, center, heading;
		public double timer, alarm, spin;
		public Mulberry32 rng;
		public bool active;
		public double radius = 4; // bounding radius of the group (m), updated while simulated
		public double ball; // bait: 0 cruising school .. 1 milling ball; escort: 0 .. 1 scattering as the whale breaks the surface
		public bool attached; // escort: the slots are placed on the whale
		public double rest; // stingray: resting time left
		public double breath; // turtle: time to the next breath
		public double jumpTimer; // mullet: time to the next leap
		public int pathIndex, pathDir = 1;

		public FishGroup( Behaviour sp, int count, int offset, FishZone zone, Mulberry32 rng )
		{
			this.sp = sp; this.count = count; this.offset = offset; this.zone = zone;
			home = new Vector3( zone.x, 0, zone.z );
			goal = home.clone();
			center = home.clone();
			heading = new Vector3( 1, 0, 0 );
			spin = rng.Next() < 0.5 ? 1 : - 1;
			this.rng = rng;
			breath = 30 + rng.Next() * 40;
			jumpTimer = 3 + rng.Next() * 6;
		}
	}

	public sealed class FishSchools
	{
		const double TAU = Math.PI * 2;
		const double FLOOR_CLEARANCE = 0.18; // minimum above the reef / seabed (m)
		public const double RANGE = 45; // draw distance (m)
		public const double SIM_RANGE = 65; // groups further than this from the camera are frozen (m)
		const double GRAVITY = 9.81;
		public static readonly double[] LOD_PX = { 320, 80, 22 }; // screen length (px) below which the next level of detail takes over
		public const double LOD_BAND = 1.15; // cross-fade band above each switch (screen length ratio)

		static readonly Dictionary<string, Behaviour> BEHAVIOUR = FishBehaviour.BEHAVIOUR;

		public readonly TerrainData terrain;
		public readonly Func<double, double, double> floorAt;
		public readonly Vector3 center;
		public readonly double radius;
		public readonly List<double[]> anchors;
		public readonly ShoreFieldData shoreField;
		public readonly Mulberry32 rng;
		public double time;
		// set by the owner (Spray): splashes of leaping mullet ( position, velocity, particles, size )
		public Action<Vector3, Vector3, int, double> sprayEmit;
		public readonly List<FishGroup> groups = new List<FishGroup>();
		public int n;
		public List<double[]> dropPath;
		public List<FishGroup> baitGroups;
		public FishGroup[] escort;
		// the humpback (set by its view): the escort follows the posed body
		public Marine.Whale whale;
		public void setWhale( Marine.Whale w )
		{
			if ( w == whale ) return;
			whale = w;
			foreach ( var g in escort ) g.attached = false; // the fish place themselves on the new body
		}
		public readonly List<string> models; // the distinct models in order of first use; kind = 4 * index + level of detail
		public readonly Dictionary<string, int> modelKind = new Dictionary<string, int>();

		// ---- fish state
		public readonly float[] pos, vel, head, roll, bend, phase, size, speedMul, panic, seed, slot, jump, floorC, prev, tmpA;
		public readonly int[] kind;
		public readonly byte[] pattern, shallowC;
		public readonly ushort[] tmpC;

		public int frame;
		public int cullFrame = - 1;
		public double dt = 1.0 / 60;
		public bool anyActive;
		public bool calm; // (Unity only) the wildlife camera: the camera still wakes the groups around it, but it frightens nothing
		public FishBatch batch;
		float[] breakGrid;

		// floorAt( x, z ): highest obstacle (seabed or reef); anchors: coral heads; bay: also populate the bay, the pier and the beach;
		// shoreField (optional) locates the breakers (without it every beach counts as fully exposed)
		public FishSchools( TerrainData terrain, Vector3 center, double radius, Func<double, double, double> floorAt = null, List<double[]> anchors = null,
			uint rngSeed = 23, bool bay = true, ShoreFieldData shoreField = null )
		{
			this.terrain = terrain;
			this.floorAt = floorAt ?? terrain.HeightAt;
			this.center = center.clone();
			this.radius = radius;
			this.anchors = anchors != null && anchors.Count > 0 ? anchors : new List<double[]> { new[] { center.x, center.z } };
			this.shoreField = shoreField;
			rng = new Mulberry32( rngSeed );

			layout( bay );

			models = new List<string>();
			foreach ( var g in groups ) if ( ! models.Contains( g.sp.model ) ) models.Add( g.sp.model );
			for ( int m = 0; m < models.Count; m ++ ) modelKind[ models[ m ] ] = m * 4;

			int N = n;
			pos = new float[ N * 3 ]; vel = new float[ N * 3 ]; head = new float[ N * 3 ]; prev = new float[ N * 3 ];
			roll = new float[ N ]; bend = new float[ N ]; phase = new float[ N ]; size = new float[ N ]; speedMul = new float[ N ]; panic = new float[ N ]; seed = new float[ N ];
			kind = new int[ N ];
			pattern = new byte[ N ];
			slot = new float[ N * 4 ]; // bait: formation slot (unit direction, radius); pile: pile index
			jump = new float[ N ]; // mullet: 0 swimming, 1 rising to the surface, 2 in the air
			floorC = new float[ N ]; Array.Fill( floorC, - 1000f ); // cached bottom under / ahead of each fish
			shallowC = new byte[ N ]; // cached: shallow water or breakers ahead
			tmpA = new float[ N * 9 ]; // boids accumulators: separation, alignment, cohesion
			tmpC = new ushort[ N ]; // neighbour counts
			foreach ( var g in groups ) initGroup( g );

			batch = new FishBatch( models.Count * 4, N );
			warmUp();
		}

		public int fishCount => n;

		// the models per level of detail (0 nearest .. 3): the rays and the turtle share their second level
		public static BufferGeometry[] modelGeometries( string name )
		{
			if ( name == "stingray" || name == "eagleRay" )
			{
				bool eagle = name == "eagleRay";
				var g1 = CreatureGeometry.rayGeometry( 1, eagle );
				// the eagle ray is the modelled asset close up (two levels of detail), the procedural ray far off
				return eagle ? new[] { CreatureGeometry.eagleRayGeometry( 0 ), CreatureGeometry.eagleRayGeometry( 1 ), g1, g1 } : new[] { CreatureGeometry.rayGeometry( 0, eagle ), g1, g1, g1 };
			}

			if ( name == "turtle" )
			{
				var g1 = CreatureGeometry.turtleGeometry( 1 );
				return new[] { CreatureGeometry.turtleGeometry( 0 ), g1, g1, g1 };
			}

			var S = FishSpecies.SPECIES[ name ];
			var geos = new BufferGeometry[ 4 ];
			for ( int lod = 0; lod < 4; lod ++ ) geos[ lod ] = FishGeometry.fishGeometry( S, new FishGeoOpts { lod = lod, pose = "swim" } );
			return geos;
		}

		static double Hypot( double a, double b ) => JS.Hypot( a, b );
		static double Hypot( double a, double b, double c ) => JS.Hypot( a, b, c );

		// ------------------------------------------------------------------ layout

		double depthAt( double x, double z ) => - terrain.HeightAt( x, z );

		// Deepest water where waves of the default swell may break at x, z (m): 0 for sheltered water.
		public double breakDepth( double x, double z )
		{
			double e = 1;
			var F = shoreField;
			if ( F != null )
			{
				int i = ( int ) Math.Floor( ( x - F.origin ) / F.cellSize ), j = ( int ) Math.Floor( ( z - F.origin ) / F.cellSize );
				if ( i >= 0 && j >= 0 && i < F.res && j < F.res )
				{
					int k = ( j * F.res + i ) * 4;
					e = Math.Min( 1, Hypot( F.data[ k + 1 ], F.data[ k + 2 ] ) );
				}
			}

			// the larger waves of a set (ShoreWaves: offshore amplitude 0.42 m, sets up to ~1.6x)
			double A = 0.42 * 1.3 * e;
			return Math.Pow( A * 3.556 / 0.78, 0.8 );
		}

		// water deep enough for the species, clear of the breakers and of the reef framework
		bool fits( Behaviour sp, FishZone zone, double x, double z )
		{
			double d = depthAt( x, z );
			if ( d < zone.band[ 0 ] || d > zone.band[ 1 ] ) return false;
			if ( d < breakDepth( x, z ) + 0.3 ) return false;
			double floor = Math.Max( terrain.HeightAt( x, z ), floorAt( x, z ) );
			return - floor > sp.minDepth + 0.3;
		}

		FishGroup addGroup( string name, int count, FishZone zone )
		{
			var sp = BEHAVIOUR[ name ];
			var g = new FishGroup( sp, count, n, zone, new Mulberry32( ( uint ) Math.Floor( rng.Next() * 1e9 ) ) );
			n += count;
			groups.Add( g );
			return g;
		}

		// Picks a spot in a zone: an anchor (coral head) in the species' depth band, or anywhere in the zone circle; far from the other homes
		// of the same kind.
		double[] pickSpot( Behaviour sp, FishZone zone, List<double[]> homes, bool anchored )
		{
			double[] best = null; double bestScore = double.NegativeInfinity;
			for ( int i = 0; i < 160; i ++ )
			{
				double x, z;
				if ( anchored && zone.anchors != null && zone.anchors.Count > 0 )
				{
					var a = zone.anchors[ ( int ) Math.Floor( rng.Next() * zone.anchors.Count ) ];
					double t = rng.Next() * TAU, r = rng.Next() * 2;
					x = a[ 0 ] + Math.Cos( t ) * r;
					z = a[ 1 ] + Math.Sin( t ) * r;
				}
				else
				{
					double t = rng.Next() * TAU, r = Math.Sqrt( rng.Next() ) * zone.r;
					x = zone.x + Math.Cos( t ) * r;
					z = zone.z + Math.Sin( t ) * r;
				}

				if ( ! fits( sp, zone, x, z ) ) continue;
				double score = rng.Next();
				foreach ( var o in homes )
				{
					double d = Hypot( o[ 0 ] - x, o[ 1 ] - z );
					if ( d < 10 ) score -= ( 10 - d ) * 0.3;
				}

				if ( score > bestScore ) { bestScore = score; best = new[] { x, z }; }
			}

			return best;
		}

		void layout( bool bay )
		{
			var reef = new FishZone { x = center.x, z = center.z, r = radius * 0.75, anchors = anchors };
			var homes = new List<double[]>();
			FishGroup place( string name, int count, FishZone zone, bool anchored = false )
			{
				var sp = BEHAVIOUR[ name ];
				var z = zone.Clone();
				z.band = zone.band ?? sp.band;
				var spot = pickSpot( sp, z, homes, anchored );
				if ( spot == null ) return null;
				homes.Add( spot );
				var zz = z.Clone();
				zz.x = spot[ 0 ]; zz.z = spot[ 1 ]; zz.r = Math.Min( z.r, sp.homeRadius * 1.5 + 4 );
				return addGroup( name, count, zz );
			}

			FishZone zoneWith( FishZone zone, double? x = null, double? z = null, double? r = null, double[] band = null, bool? sand = null )
			{
				var c = zone.Clone();
				if ( x.HasValue ) c.x = x.Value;
				if ( z.HasValue ) c.z = z.Value;
				if ( r.HasValue ) c.r = r.Value;
				if ( band != null ) c.band = band;
				if ( sand.HasValue ) c.sand = sand.Value;
				return c;
			}

			// ---- reef, by depth band
			foreach ( int c in new[] { 14, 12, 10, 12 } ) place( "sergeant", c, reef, true );
			foreach ( int c in new[] { 26, 22, 20, 24, 18, 22 } ) place( "chromis", c, reef, true );
			foreach ( int c in new[] { 28, 22, 20, 16, 16 } ) place( "grunt", c, reef, true );
			foreach ( int c in new[] { 16, 12 } ) place( "yellowtail", c, reef );
			foreach ( int c in new[] { 18, 12 } ) place( "tang", c, reef );
			foreach ( int c in new[] { 16, 12, 12, 14 } ) place( "wrasse", c, reef );
			foreach ( int c in new[] { 4, 3, 3, 3 } ) place( "parrot", c, reef );
			for ( int i = 0; i < 3; i ++ ) place( "angel", 2, reef, true );
			for ( int i = 0; i < 2; i ++ ) place( "grouper", 1, reef, true );
			for ( int i = 0; i < 2; i ++ ) place( "barracuda", 1, reef );
			foreach ( int c in new[] { 150, 120 } ) place( "silverside", c, reef );

			// ---- the drop-off: the reef's deep seaward slope
			var drop = dropOff();
			dropPath = drop;
			var mid = drop.Count > 0 ? drop[ drop.Count / 2 ] : new[] { reef.x, reef.z + reef.r };
			var dropZone = new FishZone { x = mid[ 0 ], z = mid[ 1 ], r = 30, path = drop };
			var ball = place( "bait", 900, zoneWith( dropZone, r: 14 ) );
			baitGroups = ball != null ? new List<FishGroup> { ball } : new List<FishGroup>();
			foreach ( int c in new[] { 9, 6 } )
			{
				var g = place( "jack", c, dropZone );
				if ( g != null ) g.zone.path = drop;
			}

			var eagle = place( "eagleRay", 1, dropZone );
			if ( eagle != null ) eagle.zone.path = drop;
			for ( int i = 0; i < 2; i ++ ) place( "stingray", 1, zoneWith( reef, r: radius, band: new[] { 3.0, 12 }, sand: true ) );
			place( "turtle", 1, zoneWith( reef, r: radius, band: new[] { 3.0, 10 } ) );

			if ( ! bay ) return;

			// ---- the bay (east of the reef, south of the beach)
			var bayZone = new FishZone { x = 45, z = 70, r = 45 };
			place( "tarpon", 3, zoneWith( bayZone, band: new[] { 5.0, 14 } ) );
			place( "jack", 5, new FishZone { x = WorldLayout.Pier.x, z = WorldLayout.Pier.zEnd, r = 20, band = new[] { 3.0, 10 } } );
			place( "stingray", 1, zoneWith( bayZone, band: new[] { 3.0, 9 }, sand: true ) );
			place( "barracuda", 1, new FishZone { x = WorldLayout.Pier.x + 6, z = WorldLayout.Pier.zEnd - 4, r = 8, band = new[] { 3.0, 8 } } );
			foreach ( int c in new[] { 3, 2, 1 } ) place( "needlefish", c, new FishZone { x = 30, z = 30, r = 60, band = new[] { 2.0, 12 } } );
			foreach ( int c in new[] { 9, 7 } ) place( "mullet", c, new FishZone { x = 20, z = 5, r = 70 } );

			// fry along the beach, just outside the breakers (or inside sheltered water)
			foreach ( var cx in new[] { ( 90, - 60.0 ), ( 70, - 10 ), ( 80, 30 ), ( 60, 110 ), ( 70, 150 ) } ) place( "fry", cx.Item1, new FishZone { x = cx.Item2, z = 0, r = 30 } );

			// snappers, grunts and sergeant majors around the pier piles (in water deep enough)
			var piles = new List<double[]>();
			double off = WorldLayout.Pier.width / 2 + 0.17;
			for ( double z = WorldLayout.Pier.zStart + 1.5; z < WorldLayout.Pier.zEnd - WorldLayout.Pier.headDepth; z += 3 ) foreach ( int s in new[] { - 1, 1 } ) piles.Add( new[] { WorldLayout.Pier.x + s * off, z } );
			for ( int k = 0; k < 5; k ++ )
				foreach ( double z in new[] { WorldLayout.Pier.zEnd - WorldLayout.Pier.headDepth + 0.25, WorldLayout.Pier.zEnd - WorldLayout.Pier.headDepth / 2, WorldLayout.Pier.zEnd - 0.25 } )
					piles.Add( new[] { WorldLayout.Pier.x - WorldLayout.Pier.headWidth / 2 + 0.3 + k * ( WorldLayout.Pier.headWidth - 0.6 ) / 4, z } );
			// the whale's escort, waiting until a whale is set (setWhale)
			escort = new[] { ( "pilot", 12 ), ( "juvenile", 6 ), ( "remora", 4 ) }.Select( e => addGroup( e.Item1, e.Item2, new FishZone { x = 0, z = 200, r = 5, band = new[] { 0.0, 1e9 } } ) ).ToArray();
			var wet = piles.FindAll( p => depthAt( p[ 0 ], p[ 1 ] ) > 2.2 );
			var pierZone = new FishZone { x = WorldLayout.Pier.x, z = WorldLayout.Pier.zEnd - 8, r = 18, anchors = wet, piles = wet };
			foreach ( var e in new[] { ( "pierGrunt", 14 ), ( "pierSnapper", 10 ), ( "pierSergeant", 12 ) } )
			{
				var g = place( e.Item1, e.Item2, pierZone, true );
				// the piles around its home
				if ( g != null ) g.zone.piles = wet.FindAll( p => Hypot( p[ 0 ] - g.home.x, p[ 1 ] - g.home.z ) < 5 );
				if ( g != null && g.zone.piles.Count == 0 ) g.zone.piles = new List<double[]> { new[] { g.home.x, g.home.z } };
			}
		}

		// Points along the reef's seaward slope (10 - 15 m deep), ordered along it.
		List<double[]> dropOff()
		{
			var pts = new List<double[]>();
			var c = center;
			for ( int a = 0; a < 64; a ++ )
			{
				double t = a / 64.0 * TAU;
				double dx = Math.Cos( t ), dz = Math.Sin( t );
				// walk outward from the reef centre to where the water gets 11 m deep
				for ( double r = 10; r < radius * 1.4; r += 2 )
				{
					double x = c.x + dx * r, z = c.z + dz * r;
					double d = depthAt( x, z );
					if ( d >= 11 )
					{
						if ( d < 18 ) pts.Add( new[] { x, z, t } );
						break;
					}
				}
			}

			// (sorted by angle: already so)
			pts.Sort( ( a, b ) => a[ 2 ].CompareTo( b[ 2 ] ) );
			return pts.ConvertAll( p => new[] { p[ 0 ], p[ 1 ] } );
		}

		void initGroup( FishGroup g )
		{
			var sp = g.sp; var r = g.rng;
			double dir = r.Next() * TAU;
			g.heading.set( Math.Cos( dir ), 0, Math.Sin( dir ) );
			double spread = sp.mode == "school" || sp.mode == "fry" || sp.mode == "jumper" ? 1.5 : sp.mode == "bait" ? 3
				: sp.mode == "solo" || sp.mode == "lurk" || sp.mode == "glide" || sp.mode == "turtle" || sp.mode == "cruise" ? 0 : 1.2;
			g.home.y = depthFor( sp, g.home.x, g.home.z, 0.5 );
			g.goal.copy( g.home );
			g.center.copy( g.home );
			int kind0 = modelKind[ sp.model ];
			int pat = FishSpecies.SPECIES[ sp.model ].pattern;
			for ( int k = 0; k < g.count; k ++ )
			{
				int i = g.offset + k;
				double x = g.home.x, z = g.home.z;
				for ( int t = 0; t < 10; t ++ )
				{
					double tx = g.home.x + ( r.Next() - 0.5 ) * spread * 2, tz = g.home.z + ( r.Next() - 0.5 ) * spread * 2;
					if ( depthAt( tx, tz ) > sp.minDepth + 0.3 ) { x = tx; z = tz; break; }
				}

				double y = clampY( sp, x, z, g.home.y + ( r.Next() - 0.5 ) * spread * 0.5 );
				double L = sp.length[ 0 ] + ( sp.length[ 1 ] - sp.length[ 0 ] ) * r.Next();
				size[ i ] = ( float ) L;
				set3( pos, i, x, y, z );
				set3( prev, i, x, y, z );
				double s = sp.cruise * L;
				double d = dir + ( r.Next() - 0.5 ) * 0.5;
				set3( vel, i, Math.Cos( d ) * s, 0, Math.Sin( d ) * s );
				set3( head, i, Math.Cos( d ), 0, Math.Sin( d ) );
				phase[ i ] = ( float ) ( r.Next() * TAU );
				speedMul[ i ] = ( float ) ( 0.85 + r.Next() * 0.3 );
				seed[ i ] = ( float ) r.Next();
				kind[ i ] = kind0;
				pattern[ i ] = ( byte ) pat;
				// bait formation slot: random direction, radius biased outward (a hollow-ish ball)
				double u = r.Next() * 2 - 1, a = r.Next() * TAU, rr = Math.Sqrt( 1 - u * u );
				slot[ i * 4 ] = ( float ) ( rr * Math.Cos( a ) ); slot[ i * 4 + 1 ] = ( float ) u; slot[ i * 4 + 2 ] = ( float ) ( rr * Math.Sin( a ) );
				slot[ i * 4 + 3 ] = ( float ) ( 0.45 + 0.55 * Math.Cbrt( r.Next() ) );
				if ( sp.mode == "pile" && g.zone.piles != null ) slot[ i * 4 + 3 ] = ( float ) Math.Floor( r.Next() * g.zone.piles.Count );
			}

			retarget( g, null );
		}

		static void set3( float[] a, int i, double x, double y, double z ) { a[ i * 3 ] = ( float ) x; a[ i * 3 + 1 ] = ( float ) y; a[ i * 3 + 2 ] = ( float ) z; }

		// preferred swimming height at x, z: depth[ 0 .. 1 ] of the water column above the bottom
		double depthFor( Behaviour sp, double x, double z, double k )
		{
			double floor = Math.Max( terrain.HeightAt( x, z ), floorAt( x, z ) );
			double ceil = sp.ceiling;
			if ( sp.mode == "surface" ) return ceil - 0.08;
			if ( sp.mode == "glide" ) return floor + FLOOR_CLEARANCE + 0.15;
			double f = sp.depth[ 0 ] + ( sp.depth[ 1 ] - sp.depth[ 0 ] ) * k;
			return Math.Min( ceil - 0.2, floor + FLOOR_CLEARANCE + 0.1 + Math.Max( 0, ceil - floor ) * f );
		}

		double clampY( Behaviour sp, double x, double z, double y )
		{
			double h = Math.Max( terrain.HeightAt( x, z ), floorAt( x, z ) );
			return Math.Min( sp.ceiling - 0.02, Math.Max( h + FLOOR_CLEARANCE + 0.02, y ) );
		}

		// New goal for the group: wandering in its zone, along its path, or away from a threat.
		void retarget( FishGroup g, (double x, double z)? away )
		{
			var sp = g.sp; var r = g.rng; var zone = g.zone;
			double h0, h1;
			if ( sp.mode == "solo" || sp.mode == "lurk" ) { h0 = 12; h1 = 25; }
			else if ( sp.mode == "forage" ) { h0 = 3; h1 = 7; }
			else if ( sp.mode == "surface" ) { h0 = 10; h1 = 20; }
			else { h0 = 6; h1 = 12; }
			if ( ! away.HasValue && zone.path != null && zone.path.Count > 1 && ( sp.mode == "patrol" || sp.mode == "cruise" ) )
			{
				// next point along the drop-off, back and forth
				int i = g.pathIndex + g.pathDir * ( 2 + ( int ) Math.Floor( r.Next() * 3 ) );
				if ( i < 0 || i >= zone.path.Count )
				{
					g.pathDir = - g.pathDir;
					i = Math.Max( 0, Math.Min( zone.path.Count - 1, g.pathIndex + g.pathDir * 2 ) );
				}

				g.pathIndex = i;
				var p = zone.path[ i ];
				g.goal.set( p[ 0 ], depthFor( sp, p[ 0 ], p[ 1 ], r.Next() ), p[ 1 ] );
				g.timer = h0 + ( h1 - h0 ) * r.Next();
				return;
			}

			for ( int k = 0; k < 24; k ++ )
			{
				double x, z;
				if ( away.HasValue && k < 16 )
				{
					double a = Math.Atan2( away.Value.z, away.Value.x ) + ( r.Next() - 0.5 ) * ( 0.6 + k * 0.15 );
					double d = 6 + r.Next() * 6;
					x = g.center.x + Math.Cos( a ) * d;
					z = g.center.z + Math.Sin( a ) * d;
				}
				else if ( sp.mode == "surface" )
				{
					// long straight runs
					double a = Math.Atan2( g.heading.z, g.heading.x ) + ( r.Next() - 0.5 ) * 1.6;
					double d = 15 + r.Next() * 20;
					x = g.center.x + Math.Cos( a ) * d;
					z = g.center.z + Math.Sin( a ) * d;
					if ( Hypot( x - zone.x, z - zone.z ) > zone.r ) continue;
				}
				else
				{
					double a = r.Next() * TAU, rad = Math.Sqrt( r.Next() ) * Math.Max( sp.homeRadius, zone.r * 0.8 );
					x = g.home.x + Math.Cos( a ) * rad;
					z = g.home.z + Math.Sin( a ) * rad;
				}

				if ( ! fits( sp, zone, x, z ) ) continue;
				if ( zone.sand && floorAt( x, z ) > terrain.HeightAt( x, z ) + 0.1 ) continue; // rays: over sand
				g.goal.set( x, depthFor( sp, x, z, r.Next() ), z );
				g.timer = h0 + ( h1 - h0 ) * r.Next();
				return;
			}

			g.goal.copy( g.home );
			g.timer = 4;
		}

		// Runs the steering branches once so the JIT doesn't hitch on the first encounter (here it also keeps the random sequence of the JS).
		void warmUp()
		{
			var sPos = ( float[] ) pos.Clone(); var sVel = ( float[] ) vel.Clone(); var sHead = ( float[] ) head.Clone();
			var sPanic = ( float[] ) panic.Clone(); var sJump = ( float[] ) jump.Clone();
			var goals = groups.ConvertAll( g => new object[] { g.goal.clone(), g.timer, g.alarm, g.ball, g.rest, g.breath, g.jumpTimer } );
			var probe = new Vector3();
			for ( int k = 0; k < 40; k ++ )
				foreach ( var g in groups )
				{
					probe.copy( g.center );
					probe.x += 1.5;
					stepGroup( g, 1.0 / 60, k % 2 != 0 ? probe : null );
				}

			Array.Copy( sPos, pos, sPos.Length ); Array.Copy( sPos, prev, sPos.Length );
			Array.Copy( sVel, vel, sVel.Length ); Array.Copy( sHead, head, sHead.Length );
			Array.Copy( sPanic, panic, sPanic.Length ); Array.Copy( sJump, jump, sJump.Length );
			for ( int i = 0; i < groups.Count; i ++ )
			{
				var g = groups[ i ]; var s = goals[ i ];
				g.goal.copy( ( Vector3 ) s[ 0 ] );
				g.timer = ( double ) s[ 1 ]; g.alarm = ( double ) s[ 2 ]; g.ball = ( double ) s[ 3 ]; g.rest = ( double ) s[ 4 ]; g.breath = ( double ) s[ 5 ]; g.jumpTimer = ( double ) s[ 6 ];
				g.center.copy( g.home );
			}
		}

		// ------------------------------------------------------------------ simulation

		readonly Vector3 _v = new Vector3(), _threat = new Vector3(), _w = new Vector3();

		// player: the camera position (sim). Fish flee from it underwater, and from the legs of someone wading in the shallows.
		public void update( double dt, Vector3 player )
		{
			frame ++;
			time += dt;
			this.dt = dt != 0 ? dt : 1.0 / 60;
			Vector3 threat = null;
			if ( player != null && ! calm )
			{
				if ( player.y < - 0.1 ) threat = player;
				else
				{
					double ground = terrain.HeightAt( player.x, player.z );
					if ( ground < - 0.2 && player.y - ground < 2.4 ) threat = _threat.set( player.x, Math.Max( ground + 0.3, Math.Min( - 0.3, player.y - 1.3 ) ), player.z );
				}
			}

			bool any = false;
			foreach ( var g in groups )
			{
				bool was = g.active;
				if ( g.sp.mode == "escort" || g.sp.mode == "remora" )
				{
					var w = whale;
					g.active = w != null && ( player == null || w.brain.position.distanceTo( player ) < SIM_RANGE + 20 );
					if ( g.active && ! was ) attachEscort( g );
				}
				else g.active = player == null || Hypot( g.center.x - player.x, g.center.z - player.z ) < SIM_RANGE + g.radius;
				if ( g.active && ! was ) resume( g );
				any = any || g.active;
			}

			anyActive = any;
			if ( ! any ) return;
			Array.Copy( pos, prev, pos.Length );
			int steps = dt > 1.0 / 30 ? Math.Min( 3, ( int ) Math.Ceiling( dt * 30 ) ) : 1;
			double h = dt / steps;
			if ( dt > 0 ) for ( int k = 0; k < steps; k ++ ) foreach ( var g in groups ) if ( g.active ) stepGroup( g, h, threat );
		}

		// Escort slots in the whale's rest frame ( x, y, z, ahead ): pilot fish ahead of the head and out beside the flippers, juveniles just in front of the snout,
		// remoras on skin points of the belly (ahead < 0: rigidly attached). Placed next to the whale when it first comes into range.
		void attachEscort( FishGroup g )
		{
			var w = whale; var rng = g.rng; var S = slot;
			double L = w.zHead - w.notchZ;
			List<int> belly = null;
			if ( g.sp.mode == "remora" && w.skinPos != null )
			{
				// skin points on the belly of the front half (lowest level of detail)
				belly = new List<int>();
				int nv = w.skinPos.Length / 3;
				for ( int v = 0; v < nv; v ++ )
				{
					double z = w.skinPos[ v * 3 + 2 ];
					if ( w.skinNrm[ v * 3 + 1 ] < - 0.75 && z < w.zHead - 0.18 * L && z > w.zHead - 0.6 * L ) belly.Add( v );
				}
			}

			for ( int k = 0; k < g.count; k ++ )
			{
				int i = ( g.offset + k ) * 4;
				double side = rng.Next() < 0.5 ? - 1 : 1;
				if ( belly != null && belly.Count > 0 )
				{
					int v = belly[ ( int ) Math.Floor( rng.Next() * belly.Count ) ];
					S[ i ] = ( float ) ( w.skinPos[ v * 3 ] + w.skinNrm[ v * 3 ] * 0.12 );
					S[ i + 1 ] = ( float ) ( w.skinPos[ v * 3 + 1 ] + w.skinNrm[ v * 3 + 1 ] * 0.12 );
					S[ i + 2 ] = ( float ) ( w.skinPos[ v * 3 + 2 ] + w.skinNrm[ v * 3 + 2 ] * 0.12 );
					S[ i + 3 ] = - 1;
				}
				else if ( g.sp.name == "juvenile" )
				{
					S[ i ] = ( float ) ( side * ( 0.3 + rng.Next() * 0.9 ) );
					S[ i + 1 ] = ( float ) ( w.restYAt( w.zHead ) + ( rng.Next() - 0.6 ) * 1.2 );
					S[ i + 2 ] = ( float ) w.zHead;
					S[ i + 3 ] = ( float ) ( 0.6 + rng.Next() * 1.2 );
				}
				else if ( rng.Next() < 0.55 )
				{
					S[ i ] = ( float ) ( side * ( 0.4 + rng.Next() * 1.8 ) );
					S[ i + 1 ] = ( float ) ( w.restYAt( w.zHead ) + ( rng.Next() - 0.5 ) * 1.6 );
					S[ i + 2 ] = ( float ) w.zHead;
					S[ i + 3 ] = ( float ) ( 1.2 + rng.Next() * 2.8 );
				}
				else
				{
					double z = w.zHead - L * ( 0.24 + rng.Next() * 0.14 );
					S[ i ] = ( float ) ( side * ( 4.6 + rng.Next() * 2.2 ) );
					S[ i + 1 ] = ( float ) ( w.restYAt( z ) + ( rng.Next() - 0.5 ) * 1.4 );
					S[ i + 2 ] = ( float ) z;
					S[ i + 3 ] = 0;
				}

				// start at the slot
				escortTarget( i / 4, _v );
				int j = ( g.offset + k ) * 3;
				pos[ j ] = prev[ j ] = ( float ) _v.x; pos[ j + 1 ] = prev[ j + 1 ] = ( float ) _v.y; pos[ j + 2 ] = prev[ j + 2 ] = ( float ) _v.z;
				vel[ j ] = 0; vel[ j + 1 ] = 0; vel[ j + 2 ] = 0;
			}

			g.ball = 0;
			g.attached = true;
		}

		// world position of escort fish i's slot on the posed whale
		Vector3 escortTarget( int i, Vector3 o )
		{
			var w = whale; var S = slot;
			w.toWorld( _w.set( S[ i * 4 ], S[ i * 4 + 1 ], S[ i * 4 + 2 ] ), o );
			double ahead = S[ i * 4 + 3 ];
			if ( ahead > 0 ) o.add( _threat.set( 0, 0, ahead ).applyQuaternion( w.headRot ) );
			return o;
		}

		// Escort: pilot fish and juveniles keep station at their slots (and scatter while the whale breaks the surface, re-forming as it goes back down);
		// remoras ride their skin points.
		void stepEscort( FishGroup g, double dt )
		{
			var w = whale; var b = w.brain;
			var P = pos; var V = vel; var S = slot;
			int n = g.count, o = g.offset;
			double surfacing = b.state == "surface" && b.water - b.position.y < 3 ? 1 : 0;
			g.ball += ( surfacing - g.ball ) * Math.Min( 1, dt * ( surfacing > 0 ? 1.5 : 0.3 ) );
			var sp = g.sp;
			for ( int a = 0; a < n; a ++ )
			{
				int i = o + a, i3 = i * 3;
				escortTarget( i, _v );
				if ( S[ i * 4 + 3 ] < 0 )
				{
					// holding on
					V[ i3 ] = ( float ) ( ( _v.x - P[ i3 ] ) / Math.Max( dt, 1e-3 ) );
					V[ i3 + 1 ] = ( float ) ( ( _v.y - P[ i3 + 1 ] ) / Math.Max( dt, 1e-3 ) );
					V[ i3 + 2 ] = ( float ) ( ( _v.z - P[ i3 + 2 ] ) / Math.Max( dt, 1e-3 ) );
					P[ i3 ] = ( float ) _v.x; P[ i3 + 1 ] = ( float ) _v.y; P[ i3 + 2 ] = ( float ) _v.z;
					continue;
				}

				// scatter: out from the whale's axis and down
				if ( g.ball > 0.01 )
				{
					w.toWorld( _w.set( 0, S[ i * 4 + 1 ], S[ i * 4 + 2 ] ), _threat );
					double dx = _v.x - _threat.x, dz = _v.z - _threat.z, dl = Math.Sqrt( dx * dx + dz * dz ) + 1e-3;
					double kk = g.ball * ( 3 + 3 * seed[ i ] );
					_v.x += dx / dl * kk;
					_v.z += dz / dl * kk;
					_v.y -= g.ball * ( 1.5 + 2 * seed[ i ] );
				}

				_v.y = Math.Min( _v.y, b.water - 0.45 );
				double L = size[ i ];
				double vx = V[ i3 ], vy = V[ i3 + 1 ], vz = V[ i3 + 2 ];
				// spring toward the slot, matching the whale's own velocity
				const double kP = 1.8, kD = 1.6;
				vx += ( ( _v.x - P[ i3 ] ) * kP + ( b.velocity.x - vx ) * kD ) * dt;
				vy += ( ( _v.y - P[ i3 + 1 ] ) * kP + ( b.velocity.y - vy ) * kD ) * dt;
				vz += ( ( _v.z - P[ i3 + 2 ] ) * kP + ( b.velocity.z - vz ) * kD ) * dt;
				double vmax = sp.burst * L + b.velocity.length();
				double vl = Math.Sqrt( vx * vx + vy * vy + vz * vz );
				if ( vl > vmax ) { vx *= vmax / vl; vy *= vmax / vl; vz *= vmax / vl; }

				P[ i3 ] = ( float ) ( P[ i3 ] + vx * dt );
				P[ i3 + 1 ] = ( float ) Math.Min( P[ i3 + 1 ] + vy * dt, b.water - 0.35 );
				P[ i3 + 2 ] = ( float ) ( P[ i3 + 2 ] + vz * dt );
				V[ i3 ] = ( float ) vx; V[ i3 + 1 ] = ( float ) vy; V[ i3 + 2 ] = ( float ) vz;
			}
		}

		// a group coming back into range after a pause: no motion blur from the jump in time
		void resume( FishGroup g )
		{
			for ( int k = 0; k < g.count; k ++ )
			{
				int i = ( g.offset + k ) * 3;
				prev[ i ] = pos[ i ]; prev[ i + 1 ] = pos[ i + 1 ]; prev[ i + 2 ] = pos[ i + 2 ];
			}
		}

		public void stepGroup( FishGroup g, double dt, Vector3 player )
		{
			var sp = g.sp;
			int n = g.count, o = g.offset;
			var P = pos; var V = vel; var A = tmpA; var C = tmpC;
			double cx = 0, cy = 0, cz = 0;
			for ( int k = 0; k < n; k ++ )
			{
				int i = ( o + k ) * 3;
				cx += P[ i ]; cy += P[ i + 1 ]; cz += P[ i + 2 ];
			}

			g.center.set( cx / n, cy / n, cz / n );
			double r2 = 0;
			for ( int k = 0; k < n; k += Math.Max( 1, n >> 4 ) )
			{
				int i = ( o + k ) * 3;
				double ex = P[ i ] - g.center.x, ey = P[ i + 1 ] - g.center.y, ez = P[ i + 2 ] - g.center.z;
				r2 = Math.Max( r2, ex * ex + ey * ey + ez * ez );
			}

			g.radius = Math.Sqrt( r2 ) + 2;
			g.timer -= dt;
			g.alarm = Math.Max( 0, g.alarm - dt );
			if ( sp.mode == "bait" ) { stepBait( g, dt, player ); return; }
			if ( sp.mode == "escort" || sp.mode == "remora" ) { if ( whale != null && g.attached ) stepEscort( g, dt ); return; }

			double ggx = g.goal.x - g.center.x, ggz = g.goal.z - g.center.z;
			bool roams = sp.mode != "mill" && sp.mode != "hover" && sp.mode != "pile" && sp.mode != "lurk";
			if ( g.timer <= 0 || ( roams && ggx * ggx + ggz * ggz < 1.5 ) ) retarget( g, null );

			// jacks hunt the bait ball when it is near
			FishGroup hunt = null;
			if ( sp.mode == "patrol" )
				foreach ( var b in baitGroups )
					if ( b.active && Hypot( b.center.x - g.center.x, b.center.z - g.center.z ) < 30 ) hunt = b;

			// turtle: up to the surface for a breath now and then
			if ( sp.mode == "turtle" )
			{
				g.breath -= dt;
				if ( g.breath < 0 && g.breath + dt >= 0 )
				{
					g.goal.set( g.center.x + g.heading.x * 4, sp.ceiling, g.center.z + g.heading.z * 4 );
					g.timer = 14;
				}

				if ( g.breath < - 12 )
				{
					g.breath = 40 + g.rng.Next() * 50;
					retarget( g, null );
				}
			}

			// stingray: rests on the sand now and then
			if ( sp.mode == "glide" )
			{
				if ( g.rest > 0 ) g.rest -= dt;
				else if ( g.rng.Next() < dt * 0.02 ) g.rest = 6 + g.rng.Next() * 12;
			}

			// the diver
			double px = 0, py = 0, pz = 0; bool near = false;
			if ( player != null )
			{
				px = player.x; py = player.y; pz = player.z;
				double dx = g.center.x - px, dy = g.center.y - py, dz = g.center.z - pz;
				double d2 = dx * dx + dy * dy + dz * dz;
				double reach = sp.flee + 4;
				double rr = reach + 20 + g.radius;
				near = d2 < rr * rr;
				if ( near && g.alarm <= 0 && d2 < reach * reach && roams )
				{
					g.alarm = 4;
					g.rest = 0;
					retarget( g, ( dx, dz ) );
				}
			}

			// neighbours (symmetric, within the group)
			double nr = sp.nbr * sp.length[ 1 ];
			double nbr2 = nr * nr, sepR = sp.sep * sp.length[ 1 ], sep2 = sepR * sepR;
			int a0 = o * 9;
			Array.Clear( A, a0, n * 9 );
			Array.Clear( C, o, n );
			if ( n > 1 && ( sp.wAli > 0 || sp.wCoh > 0 || sp.wSep > 0 ) )
			{
				int stride = n > 60 ? 7 : 1; // large schools: a strided subset of neighbours
				for ( int a = 0; a < n; a ++ )
				{
					int i = o + a;
					double ix = P[ i * 3 ], iy = P[ i * 3 + 1 ], iz = P[ i * 3 + 2 ];
					for ( int b = a + 1; b < n; b += stride )
					{
						int j = o + b;
						double dx = P[ j * 3 ] - ix, dy = P[ j * 3 + 1 ] - iy, dz = P[ j * 3 + 2 ] - iz;
						double d2 = dx * dx + dy * dy + dz * dz;
						if ( d2 > nbr2 ) continue;
						C[ i ] ++; C[ j ] ++;
						int ai = i * 9, aj = j * 9;
						A[ ai + 3 ] = ( float ) ( A[ ai + 3 ] + ( double ) V[ j * 3 ] ); A[ ai + 4 ] = ( float ) ( A[ ai + 4 ] + ( double ) V[ j * 3 + 1 ] ); A[ ai + 5 ] = ( float ) ( A[ ai + 5 ] + ( double ) V[ j * 3 + 2 ] );
						A[ aj + 3 ] = ( float ) ( A[ aj + 3 ] + ( double ) V[ i * 3 ] ); A[ aj + 4 ] = ( float ) ( A[ aj + 4 ] + ( double ) V[ i * 3 + 1 ] ); A[ aj + 5 ] = ( float ) ( A[ aj + 5 ] + ( double ) V[ i * 3 + 2 ] );
						A[ ai + 6 ] = ( float ) ( A[ ai + 6 ] + dx ); A[ ai + 7 ] = ( float ) ( A[ ai + 7 ] + dy ); A[ ai + 8 ] = ( float ) ( A[ ai + 8 ] + dz );
						A[ aj + 6 ] = ( float ) ( A[ aj + 6 ] - dx ); A[ aj + 7 ] = ( float ) ( A[ aj + 7 ] - dy ); A[ aj + 8 ] = ( float ) ( A[ aj + 8 ] - dz );
						if ( d2 < sep2 )
						{
							double d = Math.Sqrt( d2 ) + 1e-4;
							double f = ( sepR - d ) / ( sepR * d );
							A[ ai ] = ( float ) ( A[ ai ] - dx * f ); A[ ai + 1 ] = ( float ) ( A[ ai + 1 ] - dy * f ); A[ ai + 2 ] = ( float ) ( A[ ai + 2 ] - dz * f );
							A[ aj ] = ( float ) ( A[ aj ] + dx * f ); A[ aj + 1 ] = ( float ) ( A[ aj + 1 ] + dy * f ); A[ aj + 2 ] = ( float ) ( A[ aj + 2 ] + dz * f );
						}
					}
				}
			}

			double t = time;
			double flee2 = sp.flee * sp.flee;
			var piles = g.zone.piles;
			for ( int a = 0; a < n; a ++ )
			{
				int i = o + a, i3 = i * 3, ai = i * 9;
				double L = size[ i ];
				double x = P[ i3 ], y = P[ i3 + 1 ], z = P[ i3 + 2 ];
				double vx = V[ i3 ], vy = V[ i3 + 1 ], vz = V[ i3 + 2 ];

				// a leaping mullet flies (and falls back) on its own
				if ( jump[ i ] > 1.5 )
				{
					vy -= GRAVITY * dt;
					x += vx * dt; y += vy * dt; z += vz * dt;
					if ( y < - 0.05 && vy < 0 )
					{
						jump[ i ] = 0;
						splash( x, z, vx, vz, L, 1.4 );
						vx *= 0.5; vz *= 0.5; vy *= 0.3;
					}

					P[ i3 ] = ( float ) x; P[ i3 + 1 ] = ( float ) y; P[ i3 + 2 ] = ( float ) z;
					V[ i3 ] = ( float ) vx; V[ i3 + 1 ] = ( float ) vy; V[ i3 + 2 ] = ( float ) vz;
					continue;
				}

				double cruise = sp.cruise * L * speedMul[ i ];
				double ax = A[ ai ] * sp.wSep, ay = A[ ai + 1 ] * sp.wSep, az = A[ ai + 2 ] * sp.wSep;
				int c = C[ i ];
				if ( c > 0 )
				{
					double inv = 1.0 / c;
					ax += ( A[ ai + 3 ] * inv - vx ) * sp.wAli + A[ ai + 6 ] * inv * sp.wCoh;
					ay += ( A[ ai + 4 ] * inv - vy ) * sp.wAli + A[ ai + 7 ] * inv * sp.wCoh;
					az += ( A[ ai + 5 ] * inv - vz ) * sp.wAli + A[ ai + 8 ] * inv * sp.wCoh;
				}

				// goal per behaviour
				double tx = g.goal.x, ty = g.goal.y, tz = g.goal.z;
				double want = cruise;
				double s = seed[ i ];
				if ( sp.mode == "mill" || sp.mode == "pair" )
				{
					// slow circling around the coral head
					double ang = t * ( sp.mode == "pair" ? 0.25 : 0.12 ) * g.spin + s * 0.8;
					double r = sp.homeRadius * ( 0.5 + 0.5 * s );
					tx = g.home.x + Math.Cos( ang ) * r;
					tz = g.home.z + Math.Sin( ang ) * r;
					ty = g.home.y + ( s - 0.5 ) * 0.4;
				}
				else if ( sp.mode == "hover" )
				{
					// hold a spot above the coral head, darting now and then
					double ang = s * TAU + Math.Sin( t * 0.3 + s * 9 ) * 0.4;
					double r = sp.homeRadius * Math.Sqrt( s );
					tx = g.home.x + Math.Cos( ang ) * r;
					tz = g.home.z + Math.Sin( ang ) * r;
					ty = g.home.y + ( ( s * 7.3 ) % 1 - 0.5 ) * 0.8;
					want = cruise * ( 0.3 + 0.7 * Math.Max( 0, Math.Sin( t * 0.8 + s * 20 ) ) );
				}
				else if ( sp.mode == "pile" && piles != null )
				{
					// circling the pile it belongs to, at its own height
					int pi = ( int ) slot[ i * 4 + 3 ];
					var pl = pi >= 0 && pi < piles.Count ? piles[ pi ] : piles[ 0 ];
					double ang = t * 0.2 * g.spin * ( 0.7 + s * 0.6 ) + s * TAU;
					double r = 0.5 + sp.homeRadius * ( 0.4 + 0.6 * s );
					tx = pl[ 0 ] + Math.Cos( ang ) * r;
					tz = pl[ 1 ] + Math.Sin( ang ) * r;
					double floor = terrain.HeightAt( tx, tz );
					ty = floor + FLOOR_CLEARANCE + 0.3 + ( sp.ceiling - floor ) * ( sp.depth[ 0 ] + ( sp.depth[ 1 ] - sp.depth[ 0 ] ) * ( ( s * 5.7 ) % 1 ) );
				}
				else if ( sp.mode == "lurk" )
				{
					// hangs by its coral head, turning slowly
					tx = g.home.x + Math.Cos( t * 0.05 + s * 6 ) * sp.homeRadius;
					tz = g.home.z + Math.Sin( t * 0.05 + s * 6 ) * sp.homeRadius;
					ty = g.home.y;
					want = cruise * 0.6;
				}
				else if ( hunt != null )
				{
					// jacks: circle the bait ball, now and then a pass through it
					double ang = t * 0.35 * g.spin + s * TAU;
					bool dash = Math.Sin( t * 0.4 + s * 11 ) > 0.8;
					double r = dash ? 0.5 : 5 + 2 * s;
					tx = hunt.center.x + Math.Cos( ang ) * r;
					tz = hunt.center.z + Math.Sin( ang ) * r;
					ty = hunt.center.y + ( s - 0.5 ) * 2;
					want = cruise * ( dash ? 2.2 : 1.3 );
				}
				else if ( sp.mode == "glide" && g.rest > 0 )
				{
					// resting on the sand
					tx = x;
					tz = z;
					ty = Math.Max( terrain.HeightAt( x, z ), floorAt( x, z ) ) + 0.04;
					want = 0;
				}

				double dx = tx - x, dy = ty - y, dz = tz - z;
				double dl = Math.Sqrt( dx * dx + dy * dy + dz * dz ) + 1e-6;
				double arrive = Math.Min( 1, dl / Math.Max( 0.3, L * 4 ) );
				ax += ( dx / dl * want * arrive - vx ) * sp.wGoal;
				ay += ( dy / dl * want * arrive - vy ) * sp.wGoal;
				az += ( dz / dl * want * arrive - vz ) * sp.wGoal;

				// flee from the diver (horizontally for fish near the surface)
				double pn = Math.Max( 0, panic[ i ] - dt * 0.5 );
				if ( near )
				{
					dx = x - px; dy = y - py; dz = z - pz;
					double d2 = dx * dx + dy * dy + dz * dz;
					if ( d2 < flee2 )
					{
						dl = Math.Sqrt( d2 ) + 1e-4;
						double k = 1 - dl / sp.flee;
						double f = ( k * k * 12 + k * 3 ) * sp.accel;
						ax += dx / dl * f;
						ay += dy / dl * f * 0.4;
						az += dz / dl * f;
						pn = Math.Max( pn, Math.Min( 1, k * 1.8 ) );
						g.rest = 0;
					}
				}

				panic[ i ] = ( float ) pn;

				// bottom below (with look-ahead) and the surface above; the look-ups are refreshed every fourth frame (staggered over the
				// fish): fish move a few cm in between
				if ( ( ( frame + i ) & 3 ) == 0 || floorC[ i ] < - 999 )
				{
					double lx = x + vx * 0.7, lz = z + vz * 0.7;
					floorC[ i ] = ( float ) Math.Max( floorAt( x, z ), floorAt( lx, lz ) );
					double deepAhead = - terrain.HeightAt( lx, lz );
					shallowC[ i ] = ( byte ) ( deepAhead < sp.minDepth || deepAhead < breakDepthFast( lx, lz ) ? 1 : 0 );
				}

				double clear = sp.mode == "glide" ? 0.03 : FLOOR_CLEARANCE + L * 0.8;
				double floorY = floorC[ i ] + clear;
				if ( y < floorY ) ay += ( floorY - y ) * 8;
				// shallow water ahead (or the breakers): turn back
				if ( shallowC[ i ] != 0 )
				{
					ax -= vx * 3;
					az -= vz * 3;
				}

				double ceil = sp.ceiling;
				if ( y > ceil - 0.3 && ! ( sp.mode == "jumper" && jump[ i ] > 0.5 ) ) ay -= ( y - ( ceil - 0.3 ) ) * 8;
				if ( sp.mode != "turtle" ) ay -= vy * 1.5; // fish prefer to swim level

				// mullet: rising to the surface for a leap
				if ( jump[ i ] > 0.5 )
				{
					ay += 6;
					want = cruise * 2.5;
				}

				double amax = sp.accel * L * 4 * ( 1 + pn * 3 ) * ( jump[ i ] > 0.5 ? 3 : 1 );
				double al = Math.Sqrt( ax * ax + ay * ay + az * az );
				if ( al > amax )
				{
					double k = amax / al;
					ax *= k; ay *= k; az *= k;
				}

				vx += ax * dt; vy += ay * dt; vz += az * dt;
				double speed = Math.Sqrt( vx * vx + vy * vy + vz * vz ) + 1e-6;
				double vmax = ( sp.max + ( sp.burst - sp.max ) * pn ) * L * ( jump[ i ] > 0.5 ? 2 : 1 );
				double vmin = sp.mode == "hover" || sp.mode == "solo" || sp.mode == "mill" || sp.mode == "lurk" || sp.mode == "pile" || sp.mode == "glide" ? 0.0 : cruise * 0.3;
				double sc = speed > vmax ? vmax / speed : ( speed < vmin ? vmin / speed : 1 );
				vx *= sc; vy *= sc; vz *= sc;
				if ( jump[ i ] < 0.5 )
				{
					double hs = Math.Sqrt( vx * vx + vz * vz );
					double vyMax = ( sp.mode == "turtle" ? 0.3 : 0.15 ) + hs * 0.4;
					vy = Math.Max( - vyMax, Math.Min( vyMax, vy ) );
				}

				double nx = x + vx * dt, nz = z + vz * dt;
				if ( shallowC[ i ] != 0 && - terrain.HeightAt( nx, nz ) < sp.minDepth )
				{
					nx = x; nz = z;
					vx *= - 0.5; vz *= - 0.5;
				}

				double bottom = floorC[ i ] + ( sp.mode == "glide" ? 0.02 : FLOOR_CLEARANCE );
				double ny = y + vy * dt;
				if ( jump[ i ] > 0.5 && ny > - 0.25 )
				{
					// break the surface: fly
					jump[ i ] = 2;
					double hs = Math.Sqrt( vx * vx + vz * vz ) + 1e-6;
					double k = ( 1.8 + g.rng.Next() * 0.8 ) / hs;
					vx *= k; vz *= k;
					vy = 3 + g.rng.Next() * 0.8;
					splash( nx, nz, vx, vz, L, 0.6 );
				}
				else if ( ny > ceil )
				{
					ny = ceil;
					if ( vy > 0 ) vy = 0;
				}

				if ( ny < bottom )
				{
					ny = bottom;
					if ( vy < 0 ) vy = 0;
				}

				P[ i3 ] = ( float ) nx; P[ i3 + 1 ] = ( float ) ny; P[ i3 + 2 ] = ( float ) nz;
				V[ i3 ] = ( float ) vx; V[ i3 + 1 ] = ( float ) vy; V[ i3 + 2 ] = ( float ) vz;
			}

			// mullet: now and then one of the school rises for a leap
			if ( sp.mode == "jumper" )
			{
				g.jumpTimer -= dt;
				if ( g.jumpTimer <= 0 )
				{
					g.jumpTimer = 4 + g.rng.Next() * 10;
					int i = o + ( int ) Math.Floor( g.rng.Next() * n );
					if ( jump[ i ] == 0 && pos[ i * 3 + 1 ] > - 2.5 ) jump[ i ] = 1;
				}
			}

			// the group's heading (for surface runs)
			double hvx = g.goal.x - g.center.x, hvz = g.goal.z - g.center.z, vl = Hypot( hvx, hvz );
			if ( vl > 0.5 ) g.heading.set( hvx / vl, 0, hvz / vl );
		}

		// break depth for the look-ahead, from a grid built on first use (the breakers are only near the beaches)
		double breakDepthFast( double x, double z )
		{
			if ( z >= 40 || z <= - 64 ) return 0;
			if ( breakGrid == null )
			{
				var g = breakGrid = new float[ 256 * 52 ];
				for ( int j = 0; j < 52; j ++ ) for ( int i = 0; i < 256; i ++ ) g[ j * 256 + i ] = ( float ) breakDepth( - 256 + i * 2 + 1, - 64 + j * 2 + 1 );
			}

			int ii = ( int ) Math.Floor( ( x + 256 ) * 0.5 ), jj = ( int ) Math.Floor( ( z + 64 ) * 0.5 );
			return ii < 0 || ii > 255 ? 0 : breakGrid[ jj * 256 + ii ];
		}

		// Bait ball: every fish steers to its slot in a formation around the group centre, a stretched ellipsoid along the heading while
		// cruising and a milling ball when threatened; they part around the diver and predators (fountain effect).
		void stepBait( FishGroup g, double dt, Vector3 player )
		{
			var sp = g.sp;
			int n = g.count, o = g.offset;
			var P = pos; var V = vel; var S = slot;
			var r = g.rng;
			// threats: the diver, hunting jacks
			bool threatened = false;
			double tx = 0, ty = 0, tz = 0, td = double.PositiveInfinity;
			if ( player != null )
			{
				double d = player.distanceTo( g.center );
				if ( d < 14 + g.radius )
				{
					threatened = true;
					tx = player.x; ty = player.y; tz = player.z;
					td = d;
				}
			}

			foreach ( var h in groups )
			{
				if ( h.sp.mode != "patrol" || ! h.active ) continue;
				double d = h.center.distanceTo( g.center );
				if ( d < 12 + g.radius ) threatened = true;
			}

			g.ball += ( ( threatened ? 1 : 0 ) - g.ball ) * Math.Min( 1, dt * ( threatened ? 0.8 : 0.15 ) );
			if ( g.timer <= 0 || g.center.distanceTo( g.goal ) < 3 ) retarget( g, threatened && td < 8 ? ( g.center.x - tx, g.center.z - tz ) : ( ( double, double )? ) null );

			// the centre drifts to the goal (slowly while balled)
			double L = sp.length[ 1 ];
			double speed = sp.cruise * L * ( 1 - 0.75 * g.ball ) * 3;
			_v.subVectors( g.goal, g.center );
			double dl = _v.length();
			if ( dl > 0.1 )
			{
				_v.multiplyScalar( 1 / dl );
				g.heading.lerp( _v, Math.Min( 1, dt * 0.5 ) ).normalize();
			}

			double cx = g.center.x + g.heading.x * speed * 1.5, cy = g.center.y + g.heading.y * speed, cz = g.center.z + g.heading.z * speed * 1.5;
			double Rb = 0.8 + Math.Cbrt( n ) * 0.09; // ball radius (m)
			double hx = g.heading.x, hz = g.heading.z;
			double spin = time * 0.9 * g.spin;
			double cs = Math.Cos( spin ), sn = Math.Sin( spin );
			double kP = 2.5, kD = 2.2;
			double flee = sp.flee;
			// the bottom under the ball (highest of a few samples): one estimate for all its fish
			double ballFloor = double.NegativeInfinity;
			for ( int k = 0; k < 5; k ++ )
			{
				double a = k / 5.0 * TAU, rad = k == 0 ? 0 : Rb * 2;
				ballFloor = Math.Max( ballFloor, floorAt( cx + Math.Cos( a ) * rad, cz + Math.Sin( a ) * rad ) );
			}

			ballFloor += 0.6;
			for ( int a = 0; a < n; a ++ )
			{
				int i = o + a, i3 = i * 3, i4 = i * 4;
				double sx = S[ i4 ], sy = S[ i4 + 1 ], sz = S[ i4 + 2 ], sr = S[ i4 + 3 ];
				// cruising: an ellipsoid 3 x longer along the heading
				double along = sx * 2.4, side = sz * 1.1, up = sy * 0.55;
				double cxs = cx + ( hx * along - hz * side ) * Rb, cys = cy + up * Rb, czs = cz + ( hz * along + hx * side ) * Rb;
				// balled: the slot orbits the vertical axis (milling)
				double bx = ( sx * cs - sz * sn ) * sr * Rb, bz = ( sx * sn + sz * cs ) * sr * Rb;
				double bxs = g.center.x + bx, bys = g.center.y + sy * sr * Rb * 0.8, bzs = g.center.z + bz;
				double w = g.ball;
				double tx2 = cxs + ( bxs - cxs ) * w, ty2 = cys + ( bys - cys ) * w, tz2 = czs + ( bzs - czs ) * w;
				// target velocity: along the heading while cruising, tangential while milling
				double mv = 0.9 * g.spin * sr * Rb;
				double vtx = hx * speed * ( 1 - w ) + ( - bz ) * mv * w / ( Rb + 1e-3 ), vty = 0, vtz = hz * speed * ( 1 - w ) + bx * mv * w / ( Rb + 1e-3 );
				double x = P[ i3 ], y = P[ i3 + 1 ], z = P[ i3 + 2 ];
				double vx = V[ i3 ], vy = V[ i3 + 1 ], vz = V[ i3 + 2 ];
				double pn = Math.Max( 0, panic[ i ] - dt );
				if ( player != null )
				{
					double dx = x - player.x, dy = y - player.y, dz = z - player.z;
					double d2 = dx * dx + dy * dy + dz * dz;
					if ( d2 < flee * flee )
					{
						// part around the diver: the ball opens a hole around them and closes behind
						double d = Math.Sqrt( d2 ) + 1e-3;
						double k = 1 - d / flee;
						tx2 += dx / d * k * 2.2;
						ty2 += dy / d * k * 1.2;
						tz2 += dz / d * k * 2.2;
						vtx += dx / d * k * sp.burst * L;
						vtz += dz / d * k * sp.burst * L;
						pn = Math.Max( pn, k );
					}
				}

				panic[ i ] = ( float ) pn;
				// keep off the bottom and below the surface
				ty2 = Math.Min( sp.ceiling - 0.2, Math.Max( ballFloor, ty2 ) );
				double ax = ( tx2 - x ) * kP + ( vtx - vx ) * kD + ( r.Next() - 0.5 ) * 0.6;
				double ay = ( ty2 - y ) * kP + ( vty - vy ) * kD;
				double az = ( tz2 - z ) * kP + ( vtz - vz ) * kD + ( r.Next() - 0.5 ) * 0.6;
				vx += ax * dt; vy += ay * dt; vz += az * dt;
				double vmax = ( sp.max + ( sp.burst - sp.max ) * pn ) * L;
				double vl = Math.Sqrt( vx * vx + vy * vy + vz * vz );
				if ( vl > vmax )
				{
					vx *= vmax / vl; vy *= vmax / vl; vz *= vmax / vl;
				}

				P[ i3 ] = ( float ) ( x + vx * dt ); P[ i3 + 1 ] = ( float ) ( y + vy * dt ); P[ i3 + 2 ] = ( float ) ( z + vz * dt );
				V[ i3 ] = ( float ) vx; V[ i3 + 1 ] = ( float ) vy; V[ i3 + 2 ] = ( float ) vz;
			}
		}

		// Spray where a leaping fish leaves or re-enters the water.
		void splash( double x, double z, double vx, double vz, double L, double strength )
		{
			if ( sprayEmit == null ) return;
			sprayEmit( new Vector3( x, 0.02, z ), new Vector3( vx * 0.2, 1.2 * strength, vz * 0.2 ), ( int ) JS.Round( 10 + 18 * strength ), 0.012 + L * 0.02 );
		}

		// ------------------------------------------------------------------ rendering

		// quaternion of the rotations yaw (about y), then pitch (about x), then roll (about z)
		static void yawPitchRoll( double yaw, double pitch, double roll, out double qx, out double qy, out double qz, out double qw )
		{
			double sy = Math.Sin( yaw * 0.5 ), cy = Math.Cos( yaw * 0.5 );
			double sx = Math.Sin( pitch * 0.5 ), cx = Math.Cos( pitch * 0.5 );
			double sz = Math.Sin( roll * 0.5 ), cz = Math.Cos( roll * 0.5 );
			double x1 = cy * sx, y1 = sy * cx, z1 = - sy * sx, w1 = cy * cx;
			qx = x1 * cz + y1 * sz; qy = - x1 * sz + y1 * cz; qz = w1 * sz + z1 * cz; qw = w1 * cz - z1 * sz;
		}

		// materials/LODFade.js bandFade
		public static double bandFade( double d, double start, double end )
		{
			double t = ( d - start ) / Math.Max( end - start, 1e-6 );
			t = t <= 0 ? 0 : t >= 1 ? 1 : t;
			return t * t * ( 3 - 2 * t );
		}

		// Orients the visible fish, advances their swimming wave and writes the instance data. planes: the view frustum, six planes ( nx, ny, nz,
		// d ) with inward normals in sim space, or null; pxScale: pixels per metre at 1 m (projection[1][1] * view height / 2); the state (heading,
		// bank, wave phase) advances once per update() however many cameras ask.
		public void cull( double cpx, double cpy, double cpz, double[] planes, double pxScale )
		{
			bool advance = cullFrame != frame;
			cullFrame = frame;
			double dt = this.dt;
			var P = pos; var V = vel; var H = head; var R = prev;
			var D = batch.data;
			double kHead = 1 - Math.Exp( - dt * 6 ), kRoll = 1 - Math.Exp( - dt * 3 );
			bool inFrustum( double x, double y, double z, double rad )
			{
				if ( planes == null ) return true;
				for ( int k = 0; k < 24; k += 4 ) if ( planes[ k ] * x + planes[ k + 1 ] * y + planes[ k + 2 ] * z + planes[ k + 3 ] < - rad ) return false;
				return true;
			}

			batch.begin();
			foreach ( var g in groups )
			{
				if ( ! g.active ) continue;
				var sp = g.sp;
				// whole group out of range / view
				double gd = Hypot( g.center.x - cpx, g.center.y - cpy, g.center.z - cpz );
				if ( gd > RANGE + g.radius ) continue;
				if ( ! inFrustum( g.center.x, g.center.y, g.center.z, g.radius + 2 ) ) continue;
				bool turtle = sp.mode == "turtle", ray = sp.model == "stingray" || sp.model == "eagleRay";
				for ( int a = 0; a < g.count; a ++ )
				{
					int i = g.offset + a, i3 = i * 3;
					double L = size[ i ];
					double x = P[ i3 ], y = P[ i3 + 1 ], z = P[ i3 + 2 ];
					double vx = V[ i3 ], vy = V[ i3 + 1 ], vz = V[ i3 + 2 ];
					double speed = Math.Sqrt( vx * vx + vy * vy + vz * vz ) + 1e-6;
					bool airborne = jump[ i ] > 1.5;

					// heading follows the velocity (slowly when hovering), pitch limited
					double hx, hy, hz;
					double bl = speed / L;
					bool rest = sp.mode == "glide" && g.rest > 0;
					double freq = rest ? 0.15 : Math.Min( 10, sp.freq[ 0 ] + sp.freq[ 1 ] * bl ) * ( airborne ? 2.5 : 1 );
					double dPhase = dt * TAU * freq;
					if ( advance )
					{
						double kh = airborne ? 1 : kHead * Math.Min( 1, speed / ( L * 0.5 ) + 0.15 );
						double ohx = H[ i3 ], ohz = H[ i3 + 2 ];
						hx = ohx + ( vx / speed - ohx ) * kh;
						hy = H[ i3 + 1 ] + ( vy / speed - H[ i3 + 1 ] ) * kh;
						hz = ohz + ( vz / speed - ohz ) * kh;
						double pitchMax = airborne ? 1.2 : ray || turtle ? 0.3 : 0.45;
						hy = Math.Max( - pitchMax, Math.Min( pitchMax, hy ) );
						double hl = Hypot( hx, hy, hz ); if ( hl == 0 ) hl = 1;
						hx /= hl; hy /= hl; hz /= hl;
						H[ i3 ] = ( float ) hx; H[ i3 + 1 ] = ( float ) hy; H[ i3 + 2 ] = ( float ) hz;
						double yawRate = ( ohz * hx - ohx * hz ) / dt;
						double bank = ray ? 0.45 : turtle ? 0.35 : 0.12;
						roll[ i ] = ( float ) ( roll[ i ] + ( Math.Max( - 0.5, Math.Min( 0.5, yawRate * bank ) ) - roll[ i ] ) * kRoll );
						bend[ i ] = ( float ) ( bend[ i ] + ( Math.Max( - 0.25, Math.Min( 0.25, yawRate * 0.06 ) ) - bend[ i ] ) * kRoll );
						phase[ i ] = ( float ) ( ( phase[ i ] + dPhase ) % ( TAU * 64 ) );
					}
					else { hx = H[ i3 ]; hy = H[ i3 + 1 ]; hz = H[ i3 + 2 ]; }

					// tail beat / wing wave / flipper stroke: frequency and amplitude grow with speed
					double amp = rest ? sp.amp * 0.2 : sp.amp * ( 0.55 + 0.45 * Math.Min( 2.5, bl / Math.Max( 0.2, sp.cruise ) ) + panic[ i ] * 0.5 );

					// cull: distance, size on screen and view frustum
					double dx = x - cpx, dy = y - cpy, dz = z - cpz;
					double d = Math.Sqrt( dx * dx + dy * dy + dz * dz );
					if ( d > RANGE + L ) continue;
					double px = L * pxScale / Math.Max( d, 0.1 );
					if ( px < 1.2 ) continue;
					if ( ! inFrustum( x, y, z, L * 0.7 ) ) continue;

					// orientation: yaw from the heading, pitch, bank into turns
					// (hx, hy, hz are the stored floats)
					yawPitchRoll( Math.Atan2( hx, hz ), - Math.Asin( hy ), roll[ i ], out double qx, out double qy, out double qz, out double qw );

					int o = i * 16;
					D[ o ] = ( float ) x; D[ o + 1 ] = ( float ) y; D[ o + 2 ] = ( float ) z; D[ o + 3 ] = ( float ) L;
					D[ o + 4 ] = ( float ) qx; D[ o + 5 ] = ( float ) qy; D[ o + 6 ] = ( float ) qz; D[ o + 7 ] = ( float ) qw;
					D[ o + 8 ] = phase[ i ]; D[ o + 9 ] = ( float ) amp; D[ o + 10 ] = bend[ i ]; D[ o + 11 ] = ( float ) ( pattern[ i ] + seed[ i ] * 0.9 );
					D[ o + 12 ] = ( float ) ( x - R[ i3 ] ); D[ o + 13 ] = ( float ) ( y - R[ i3 + 1 ] ); D[ o + 14 ] = ( float ) ( z - R[ i3 + 2 ] );
					D[ o + 15 ] = ( float ) dPhase;
					// level of detail by size on screen, cross-faded (dithered) over a band before each switch; faded out over the last tenth of
					// the draw distance
					int lod = px > LOD_PX[ 0 ] ? 0 : px > LOD_PX[ 1 ] ? 1 : px > LOD_PX[ 2 ] ? 2 : 3;
					int k0 = kind[ i ] + lod;
					double far = RANGE * 0.9;
					if ( d > far ) batch.addFade( k0, i, 1 - bandFade( d, far, RANGE + L ), false );
					else if ( lod < 3 && px < LOD_PX[ lod ] * LOD_BAND )
					{
						double f = bandFade( - px, - LOD_PX[ lod ] * LOD_BAND, - LOD_PX[ lod ] );
						batch.addFade( k0, i, f, true );
						batch.addFade( k0 + 1, i, f, false );
					}
					else batch.add( k0, i );
				}
			}

			batch.commit();
		}
	}
}
