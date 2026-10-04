using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.Util;
using static Tidewater.World.Village.GeoBuilder;
using static Tidewater.World.Village.Props;

// Port of src/world/Village.js: the tropical fishing village. A timber pier with a T-head, stilt fishing huts on the
// beach, a boathouse, a boardwalk up to a small market plaza and a dozen painted cottages on the slope, dressed with nets,
// traps, barrels, crates, boats and lanterns.
//
// This class builds the geometry, colliders, lights and footprints (no Unity objects): all static geometry is merged per
// material exactly as in JS (one shared opaque buffer with an index range per material, fabric and nets separate, the
// sign and the pier's lanterns as their own swinging parts). VillageView turns that into Unity meshes.
namespace Tidewater.World.Village
{
	// a swinging part (the sign, a hung lantern): geometry per material key in coordinates relative to `pivot`
	public sealed class SwingPart
	{
		public Vector3 pivot;
		public Dictionary<string, BuiltGeometry> geometry = new Dictionary<string, BuiltGeometry>();
	}

	public sealed class LanternState
	{
		public SwingPart part; public Vector3 pivot, rest, live;
		public double t, ph, x, z, vx, vz;
	}

	public sealed class OpaqueRange { public string key; public int start, count; }

	public sealed class BuildingInfo { public string name; public double x, z, floorY, roofTop; public bool stilts; public Footprint footprint; }

	public sealed class Village
	{
		// ---------------------------------------------------------------------------------- palettes
		static readonly Dictionary<string, double[]> PASTELS = new Dictionary<string, double[]>
		{
			{ "turquoise", lin( 0x5dbcb0 ) }, { "coral", lin( 0xec8b76 ) }, { "cream", lin( 0xefe2c2 ) }, { "sky", lin( 0x8cc2e0 ) }, { "yellow", lin( 0xf0cf7c ) },
			{ "mint", lin( 0xa9dcbf ) }, { "pink", lin( 0xf2b3aa ) }, { "white", lin( 0xf1ede2 ) }, { "lavender", lin( 0xbdb3da ) }, { "sea", lin( 0x6aa6b8 ) },
		};

		static readonly Dictionary<string, double[]> TRIMS = new Dictionary<string, double[]>
		{
			{ "white", lin( 0xf3efe6 ) }, { "cream", lin( 0xe9dfc6 ) }, { "navy", lin( 0x2e4f73 ) }, { "teal", lin( 0x2d7f7a ) },
			{ "red", lin( 0xa8463a ) }, { "green", lin( 0x4c7d4c ) }, { "yellow", lin( 0xe0b545 ) }, { "blue", lin( 0x3f79ae ) },
		};

		static readonly Dictionary<string, double[]> ROOFS = new Dictionary<string, double[]>
		{
			{ "red", lin( 0xa64a35 ) }, { "green", lin( 0x5f8a6a ) }, { "blue", lin( 0x4f7898 ) }, { "teal", lin( 0x3f8a86 ) }, { "rust", lin( 0x8a5a40 ) }, { "grey", lin( 0x8c9296 ) },
		};

		static readonly double[][] CURTAINS = { lin( 0xe8d6b0 ), lin( 0xd98c7a ), lin( 0x9cc0d8 ), lin( 0xf0e8d8 ), lin( 0xc9d89a ) };

		sealed class Specs { public List<Spec> houses, sheds; public Spec boathouse, stall; }

		// ---------------------------------------------------------------------------------- state
		public readonly TerrainData terrain;
		public readonly Colliders colliders;
		public readonly List<LightSource> lights = new List<LightSource>();
		public readonly List<Footprint> footprints = new List<Footprint>();
		public readonly List<FoundationCheck> foundationChecks = new List<FoundationCheck>();
		public readonly List<BuildingInfo> buildings = new List<BuildingInfo>();
		public readonly Rand rand;
		public PierInfo pierInfo;
		public BoardwalkResult path;
		public List<BoardwalkResult> sidePaths;

		// the merged result (see _assemble)
		public BuiltGeometry shared;
		public readonly List<OpaqueRange> ranges = new List<OpaqueRange>();
		public BuiltGeometry fabric, nets;
		public int shadowTriangles;
		public SwingPart sign;
		public readonly List<LanternState> lanterns = new List<LanternState>();
		public Tidewater.World.Fish.FishProps fishProps; // fish, lobsters, ice and banana leaves (market stall, drying racks, cleaning tables)

		Builder B, harbor, town, signB;
		InstancedProps inst;

		public Village( TerrainData terrain, Colliders colliders )
		{
			this.terrain = terrain;
			this.colliders = colliders;
			rand = new Rand( new Mulberry32( 90210 ) );
			// one builder: everything static merges into a single mesh per material
			B = new Builder();
			harbor = B;
			town = B;
			inst = new InstancedProps( B );

			var specs = _layout( rand );
			_flattenPads( specs );

			BuildCtx ctx( Builder b ) => new BuildCtx { B = b, terrain = terrain, colliders = colliders, rand = rand, lights = lights, inst = inst, checks = foundationChecks };

			// the fish sign at the pier entrance is its own small mesh: it swings in the wind (update())
			signB = new Builder();
			var hungBuilders = new List<Builder>();
			pierInfo = Pier.buildPier( harbor, terrain, colliders, rand, lights, inst, signB, () => { var b = new Builder(); hungBuilders.Add( b ); return b; } );
			foreach ( var h in pierInfo.hung ) { }

			// boardwalk from the foot of the pier steps up to the plaza
			var foot = pierInfo.stepFoot;
			path = Boardwalk.buildBoardwalk( ctx( harbor ), new[]
			{
				new[] { foot.x, foot.z + 0.05 }, new[] { 54.6, - 72.0 }, new[] { 52.4, - 82.0 }, new[] { 48.4, - 92.0 }, new[] { 44.8, - 100.5 }, new[] { 42.6, - 107.2 },
			}, new BoardwalkOpts { width = 1.8, startY = foot.y + 0.24, lightEvery = 70 } );
			_plaza( ctx( town ) );

			foreach ( var s in specs.houses )
			{
				var b = s.harbor ? harbor : town;
				var res = Buildings.buildHouse( ctx( b ), s );
				buildings.Add( new BuildingInfo { name = s.name, x = s.x, z = s.z, floorY = res.floorY, roofTop = res.roofTop, stilts = s.foundation == "stilts", footprint = res.footprint } );
				footprints.Add( res.footprint );
			}

			foreach ( var s in specs.sheds )
			{
				var res = Buildings.buildShed( ctx( town ), s );
				buildings.Add( new BuildingInfo { name = s.name, x = s.x, z = s.z, floorY = res.floorY, roofTop = res.roofTop, stilts = false, footprint = res.footprint } );
				footprints.Add( res.footprint );
			}

			// plank side paths from the plaza / main boardwalk to the nearest houses
			sidePaths = new List<BoardwalkResult>
			{
				Boardwalk.buildBoardwalk( ctx( town ), new[] { new[] { 36.8, - 110.6 }, new[] { 32.0, - 109.3 }, new[] { 27.4, - 107.6 } }, new BoardwalkOpts { width = 1.1, lift = 0.2, lightEvery = 1e9 } ),
				Boardwalk.buildBoardwalk( ctx( town ), new[] { new[] { 46.8, - 97.8 }, new[] { 53.5, - 98.5 }, new[] { 61.7, - 99.1 } }, new BoardwalkOpts { width = 1.1, lift = 0.2, lightEvery = 1e9 } ),
			};

			footprints.Add( Buildings.buildBoathouse( ctx( harbor ), specs.boathouse ).footprint );
			footprints.Add( Buildings.buildMarketStall( ctx( town ), specs.stall ).footprint );

			_beachProps( ctx( harbor ) );
			_villageProps( ctx( town ) );

			_assemble();
			_buildSign();
			_buildLanterns();
		}

		// ------------------------------------------------------------------ layout

		static Spec H( string name, double x, double z, double yaw, double w, double d ) => new Spec { name = name, x = x, z = z, yaw = yaw, w = w, d = d };

		Specs _layout( Rand rand )
		{
			var P = PASTELS; var T = TRIMS; var R = ROOFS;
			double[] curtain() => rand.pick( CURTAINS );
			var houses = new List<Spec>
			{
				// front row (just above the beach)
				new Spec { name = "A", x = 13.5, z = - 106.5, yaw = 0.12, w = 6.2, d = 5.0, roof = "gable", roofMat = "metal", roofColor = R[ "red" ], wall = P[ "turquoise" ], trim = T[ "white" ], accent = T[ "navy" ], siding = 1, porch = new PorchSpec { depth = 2.1, rail = "balusters" }, paint = 0.62, stovepipe = true, buoys = - 1, porchPaint = lin( 0x8a9aa0 ) },
				new Spec { name = "B", x = 26.5, z = - 112.0, yaw = - 0.08, w = 5.2, d = 4.5, roof = "hip", roofMat = "thatch", wall = P[ "coral" ], trim = T[ "cream" ], accent = T[ "teal" ], siding = 2, shutters = "bahama", paint = 0.55, thatchAge = 0.35, annex = new AnnexSpec { w = 3.0, d = 2.0, wall = lin( 0xefe2c2 ) } },
				new Spec { name = "C", x = 62.5, z = - 106.0, yaw = - 0.1, w = 7.0, d = 5.8, stories = 2, roof = "gableFront", roofMat = "metal", galv = true, rust = 0.55, wall = P[ "cream" ], trim = T[ "white" ], accent = T[ "blue" ], siding = 1, porch = new PorchSpec { depth = 2.2, rail = "x" }, paint = 0.7, antenna = true, tank = 1, doorGlass = true },
				new Spec { name = "D", x = 79.0, z = - 110.5, yaw = - 0.18, w = 6.0, d = 5.0, roof = "gable", roofMat = "metal", roofColor = R[ "green" ], wall = P[ "sky" ], trim = T[ "white" ], accent = T[ "yellow" ], siding = 2, porch = new PorchSpec { depth = 1.9, width = 4.4, offset = - 0.6, rail = "balusters" }, doorX = - 0.6, paint = 0.66, gutter = true, woodpile = 1 },
				new Spec { name = "E", x = 96.0, z = - 115.5, yaw = - 0.3, w = 5.4, d = 4.6, roof = "hip", roofMat = "metal", roofColor = R[ "teal" ], wall = P[ "yellow" ], trim = T[ "white" ], accent = T[ "green" ], siding = 1, shutters = "louver", paint = 0.58, buoys = 1, annex = new AnnexSpec { w = 3.2, d = 2.0 } },
				// middle row
				new Spec { name = "F", x = - 3.0, z = - 125.0, yaw = 0.22, w = 5.6, d = 5.0, roof = "gableFront", roofMat = "thatch", wall = P[ "mint" ], trim = T[ "white" ], accent = T[ "red" ], siding = 2, porch = new PorchSpec { depth = 1.9, rail = "x" }, paint = 0.5, thatchAge = 0.55, shutters = "board" },
				new Spec { name = "G", x = 16.5, z = - 130.0, yaw = 0.1, w = 6.6, d = 5.6, stories = 2, roof = "gable", roofMat = "metal", roofColor = R[ "blue" ], wall = P[ "pink" ], trim = T[ "white" ], accent = T[ "teal" ], siding = 1, porch = new PorchSpec { depth = 2.2, rail = "balusters" }, paint = 0.72, chimney = - 1, doorGlass = true },
				new Spec { name = "H", x = 57.5, z = - 128.5, yaw = - 0.05, w = 6.0, d = 5.2, roof = "gableFront", roofMat = "thatch", wall = P[ "turquoise" ], trim = T[ "cream" ], accent = T[ "yellow" ], siding = 2, porch = new PorchSpec { depth = 2.0, rail = "x" }, paint = 0.55, thatchAge = 0.3, shutters = "board", woodpile = - 1, annex = new AnnexSpec { w = 3.4, d = 2.2, x = 0.6, wall = lin( 0x8cc2e0 ) } },
				new Spec { name = "I", x = 75.5, z = - 132.5, yaw = - 0.2, w = 5.4, d = 4.6, roof = "gable", roofMat = "metal", roofColor = R[ "red" ], rust = 0.6, wall = P[ "cream" ], trim = T[ "red" ], accent = T[ "red" ], siding = 1, paint = 0.6, tank = - 1, stovepipe = true },
				new Spec { name = "J", x = 96.5, z = - 135.0, yaw = - 0.32, w = 6.2, d = 5.0, roof = "hip", roofMat = "metal", roofColor = R[ "grey" ], galv = true, rust = 0.7, wall = P[ "sky" ], trim = T[ "white" ], accent = T[ "navy" ], siding = 2, porch = new PorchSpec { depth = 1.9, rail = "balusters" }, paint = 0.6, annex = new AnnexSpec { w = 3.6, d = 2.0, x = - 0.8 } },
				// back row (plateau)
				new Spec { name = "K", x = 3.5, z = - 151.0, yaw = 0.18, w = 6.0, d = 5.2, roof = "gable", roofMat = "metal", roofColor = R[ "green" ], wall = P[ "yellow" ], trim = T[ "white" ], accent = T[ "blue" ], siding = 1, porch = new PorchSpec { depth = 2.0, rail = "x" }, paint = 0.64, gutter = true },
				new Spec { name = "L", x = 30.5, z = - 149.0, yaw = 0.04, w = 7.4, d = 5.6, stories = 2, roof = "hip", roofMat = "metal", roofColor = R[ "red" ], wall = P[ "white" ], trim = T[ "white" ], accent = T[ "green" ], siding = 1, porch = new PorchSpec { depth = 2.3, rail = "balusters" }, paint = 0.78, antenna = true, chimney = 1, doorGlass = true },
				new Spec { name = "M", x = 55.0, z = - 152.5, yaw = - 0.06, w = 5.2, d = 4.8, roof = "gableFront", roofMat = "thatch", wall = P[ "coral" ], trim = T[ "white" ], accent = T[ "teal" ], siding = 1, paint = 0.52, thatchAge = 0.45, shutters = "bahama", annex = new AnnexSpec { w = 3.0, d = 1.8 } },
				new Spec { name = "N", x = 78.5, z = - 156.0, yaw = - 0.22, w = 6.0, d = 5.0, roof = "gable", roofMat = "metal", roofColor = R[ "teal" ], wall = P[ "lavender" ], trim = T[ "white" ], accent = T[ "navy" ], siding = 2, porch = new PorchSpec { depth = 1.9, rail = "balusters" }, paint = 0.62, tank = 1 },
			};

			// stilt fishing huts just above the beach, close to the pier foot (floor ~3 m)
			var huts = new List<Spec>
			{
				new Spec { name = "S1", harbor = true, x = 67.8, z = - 64.6, yaw = 0.05, w = 4.0, d = 4.2, floorY = 3.0, foundation = "stilts", roof = "gableFront", roofMat = "metal", roofColor = ROOFS[ "rust" ], rust = 0.8, wall = P[ "sea" ], trim = T[ "white" ], accent = T[ "red" ], siding = 2, paint = 0.52, weather = 0.85, porch = new PorchSpec { depth = 1.6, rail = "x" }, shutters = "board", closedChance = 0.3, fewWindows = true, rimRaw = true, buoys = 1, porchBench = false, railNet = lin( 0x3f6f5f ) },
				new Spec { name = "S2", harbor = true, x = 79.8, z = - 70.4, yaw = - 0.12, w = 4.4, d = 4.0, floorY = 3.05, foundation = "stilts", roof = "gableFront", roofMat = "thatch", thatchAge = 0.6, wall = P[ "coral" ], trim = T[ "cream" ], accent = T[ "teal" ], siding = 2, paint = 0.48, weather = 0.9, porch = new PorchSpec { depth = 1.6, rail = "x" }, shutters = "board", closedChance = 0.25, fewWindows = true, rimRaw = true, porchBench = false, railNet = lin( 0x9a4a38 ) },
				new Spec { name = "S3", harbor = true, x = 37.2, z = - 72.4, yaw = 0.18, w = 4.0, d = 4.0, floorY = 3.15, foundation = "stilts", roof = "gable", roofMat = "thatch", thatchAge = 0.5, wall = P[ "yellow" ], trim = T[ "white" ], accent = T[ "blue" ], siding = 2, paint = 0.5, weather = 0.85, porch = new PorchSpec { depth = 1.6, rail = "x" }, shutters = "board", closedChance = 0.2, fewWindows = true, rimRaw = true, buoys = - 1, porchBench = false },
			};

			var all = new List<Spec>( houses ); all.AddRange( huts );
			foreach ( var h in all )
			{
				h.curtain = h.curtain ?? curtain();
				if ( h.foundation == null ) h.foundation = rand.chance( 0.5 ) ? "stone" : "posts";
				if ( h.foundation == "stone" ) h.stoneStyle = rand.chance( 0.6 ) ? 1 : 0;
			}

			var sheds = new List<Spec>
			{
				new Spec { name = "shed1", x = 21.5, z = - 119.5, yaw = 0.1, wall = lin( 0xb9c9b0 ), door = TRIMS[ "red" ] },
				new Spec { name = "shed2", x = 68.0, z = - 124.0, yaw = - 0.2, wall = lin( 0xd8b8a0 ), door = TRIMS[ "teal" ], galv = true },
				new Spec { name = "shed3", x = 89.0, z = - 146.0, yaw = - 0.3, wall = lin( 0xa8c4d4 ), door = TRIMS[ "yellow" ] },
				new Spec { name = "shed4", x = 43.5, z = - 155.5, yaw = 0.0, wall = lin( 0xe8d8a8 ), door = TRIMS[ "blue" ] },
			};

			return new Specs
			{
				houses = all, sheds = sheds,
				boathouse = new Spec { x = 92.5, z = - 57.5, yaw = - 0.08, wall = lin( 0x8fb3a8 ), paint = 0.58 },
				stall = new Spec { x = 40.2, z = - 113.2, yaw = 0.05 },
			};
		}

		// carve gentle building pads into the heightmap (must happen before anything reads heights)
		void _flattenPads( Specs specs )
		{
			var t = terrain;
			foreach ( var s in specs.houses )
			{
				if ( s.foundation == "stilts" ) continue;
				double pd = s.porch != null ? s.porch.depth : 1.0;
				double cy = Math.Cos( s.yaw ), sy = Math.Sin( s.yaw );
				double cz = pd / 2;
				double cx = s.x + cz * sy, czw = s.z + cz * cy;
				double sum = 0; int n = 0;
				for ( int i = - 2; i <= 2; i ++ )
					for ( int j = - 2; j <= 2; j ++ )
					{
						sum += t.HeightAt( cx + i * s.w.Value / 5, czw + j * ( s.d.Value + pd ) / 5 );
						n ++;
					}

				double r = JS.Hypot( s.w.Value, s.d.Value + pd ) / 2 + 0.3;
				t.Flatten( cx, czw, r, sum / n, 3.5 );
			}

			// market plaza
			{
				double px = 41.5, pz = - 111.0;
				double sum = 0; int n = 0;
				for ( int i = - 2; i <= 2; i ++ )
					for ( int j = - 2; j <= 2; j ++ )
					{
						sum += t.HeightAt( px + i, pz + j );
						n ++;
					}

				t.Flatten( px, pz, 5.5, sum / n, 4 );
			}

			t.BuildMinMax();
		}

		// ------------------------------------------------------------------ plaza

		void _plaza( BuildCtx ctx )
		{
			var B = ctx.B; var terrain = ctx.terrain; var colliders = ctx.colliders; var rand = ctx.rand; var lights = ctx.lights;
			// benches, a lamp and some barrels around the market stall
			double g( double x, double z ) => terrain.HeightAt( x, z );
			bench( B, 36.2, g( 36.2, - 108.2 ), - 108.2, 0.9, 1.6, rand.next(), lin( 0x4f8fa0 ) );
			colliders.addBox( new Vector3( 36.2, g( 36.2, - 108.2 ) + 0.45, - 108.2 ), new Vector3( 0.85, 0.45, 0.3 ), 0.9, false, true, "bench" );
			bench( B, 45.8, g( 45.8, - 108.6 ), - 108.6, - 0.85, 1.6, rand.next(), lin( 0xb05a45 ) );
			colliders.addBox( new Vector3( 45.8, g( 45.8, - 108.6 ) + 0.45, - 108.6 ), new Vector3( 0.85, 0.45, 0.3 ), - 0.85, false, true, "bench" );
			var lw = lampPost( B, 44.3, g( 44.3, - 106.2 ), - 106.2, - 2.4, 3.3, rand.next() );
			lights.Add( new LightSource { position = lw, color = new Color( 1.0, 0.72, 0.42 ), intensity = 5, kind = "lantern" } );
			colliders.addCylinder( 44.3, - 106.2, 0.1, g( 44.3, - 106.2 ), g( 44.3, - 106.2 ) + 3.4, "lampPost" );
			foundationChecks.Add( new FoundationCheck { x = 44.3, y = g( 44.3, - 106.2 ), z = - 106.2 } );
			foreach ( var p in new[] { new[] { 37.4, - 115.6 }, new[] { 38.1, - 116.1 } } )
			{
				double x = p[ 0 ], z = p[ 1 ];
				double by = g( x, z ) - 0.02, bry = rand.range( 0, 6 );
				inst.add( "barrel", x, by, z, bry, new[] { rand.range( 0.85, 1.05 ), 0.92, 0.85 } );
				colliders.addCylinder( x, z, 0.32, g( x, z ), g( x, z ) + 0.9, "barrel" );
			}

			footprints.Add( new Footprint { x = 41.5, z = - 111, r = 6, kind = "plaza" } );
		}

		// ------------------------------------------------------------------ beach props

		sealed class BoatSpec { public double x, z, ry, rz; public bool up; public double[] hull, bottom; }

		void _beachProps( BuildCtx ctx )
		{
			var B = ctx.B; var terrain = ctx.terrain; var colliders = ctx.colliders; var rand = ctx.rand;
			double g( double x, double z ) => terrain.HeightAt( x, z );
			void box( double x, double y, double z, double hx, double hy, double hz, double ry, string tag ) => colliders.addBox( new Vector3( x, y, z ), new Vector3( hx, hy, hz ), ry, false, true, tag );

			// rowboats pulled up on the sand: two upturned ones down the beach west of Joe's fish stand (kept
			// clear of it), one by the pier foot
			double BX = - 26; // the upturned pair and their oars, relative to where they first stood by the stand
			var boats = new[]
			{
				new BoatSpec { x = 46.8 + BX, z = - 57.2, ry = 0.35, up = true, hull = lin( 0x2f8f9a ), bottom = lin( 0xa0402e ) },
				new BoatSpec { x = 43.9 + BX, z = - 59.8, ry = 0.55, up = true, hull = lin( 0xe9e4d6 ), bottom = lin( 0x2e5f86 ) },
				new BoatSpec { x = 63.5, z = - 55.6, ry = - 0.35, up = false, hull = lin( 0xd8c35a ), bottom = lin( 0x3e6f5a ), rz = 0.14 },
			};
			foreach ( var b in boats )
			{
				double gy = g( b.x, b.z );
				rowboat( B, b.x, gy + ( b.up ? 0.0 : 0.05 ), b.z, b.ry, new RowboatO { upsideDown = b.up, seed = rand.next(), hull = b.hull, bottom = b.bottom, trim = C.white, rz = b.rz } );
				box( b.x, gy + 0.4, b.z, 0.72, 0.45, 2.0, b.ry, "rowboat" );
				footprints.Add( new Footprint { x = b.x, z = b.z, r = 2.4, kind = "prop" } );
			}

			// one more boat pulled up next to the boathouse, and an old wreck by the waterline
			{
				double bx = 84.6, bz = - 53.2, gy = g( bx, bz );
				rowboat( B, bx, gy + 0.03, bz, 0.45, new RowboatO { seed = rand.next(), hull = lin( 0xd0e4ea ), bottom = lin( 0xb04a30 ), trim = lin( 0x2f5f7a ), rz = - 0.16, oars = false } );
				box( bx, gy + 0.4, bz, 0.72, 0.45, 2.0, 0.45, "rowboat" );
				footprints.Add( new Footprint { x = bx, z = bz, r = 2.4, kind = "prop" } );
				double wx = 102.5, wz = - 49.8;
				wreck( B, wx, g( wx, wz ) - 0.12, wz, 0.95, rand, beam: 3.1, depth: 1.05, rz: 0.22 );
				box( wx, g( wx, wz ) + 0.4, wz, 1.3, 0.6, 3.6, 0.95, "wreck" );
				footprints.Add( new Footprint { x = wx, z = wz, r = 4, kind = "prop" } );
			}

			// oars leaning against the first upturned boat, oars on the sand
			oar( B, new[] { 48.1 + BX, g( 48.1 + BX, - 55.7 ) + 0.03, - 55.7 }, new[] { 49.6 + BX, g( 49.6 + BX, - 57.9 ) + 0.05, - 57.9 }, rand.next(), lin( 0xc23b2e ) );
			oar( B, new[] { 48.4 + BX, g( 48.4 + BX, - 55.4 ) + 0.03, - 55.4 }, new[] { 49.9 + BX, g( 49.9 + BX, - 57.6 ) + 0.06, - 57.6 }, rand.next(), lin( 0xc23b2e ) );

			// net drying racks
			var racks = new[]
			{
				new object[] { 41.8, - 64.2, 0.25, lin( 0x3f6f5f ) }, new object[] { 74.6, - 58.6, - 0.12, lin( 0x2f5f8a ) }, new object[] { 86.4, - 64.0, 0.3, lin( 0xb0553a ) },
			};
			foreach ( var r in racks )
			{
				double x = ( double ) r[ 0 ], z = ( double ) r[ 1 ], ry = ( double ) r[ 2 ]; var tint = ( double[] ) r[ 3 ];
				double cy = Math.Cos( ry ), sy = Math.Sin( ry );
				netRack( B, x, g( x, z ), z, ry, 3.2, tint, rand.next(), ( lx, lz ) => g( x + lx * cy + lz * sy, z - lx * sy + lz * cy ) );
				foreach ( double sx in new[] { - 1.6, 1.6 } )
				{
					double px = x + sx * cy, pz = z - sx * sy;
					colliders.addCylinder( px, pz, 0.1, g( px, pz ) - 0.4, g( px, pz ) + 2.0, "rack" );
					foundationChecks.Add( new FoundationCheck { x = px, y = g( px, pz ) - 0.4, z = pz } );
				}

				footprints.Add( new Footprint { x = x, z = z, r = 2.2, kind = "prop" } );
			}

			// fish drying rack between the huts
			fishRack( B, 73.8, g( 73.8, - 69.5 ), - 69.5, 0.2, 2.6, rand.next(), rand );
			box( 73.8, g( 73.8, - 69.5 ) + 1.0, - 69.5, 1.45, 1.0, 0.75, 0.2, "rack" );
			foundationChecks.Add( new FoundationCheck { x = 73.8, y = g( 73.8, - 69.5 ) - 0.2, z = - 69.5 } );

			// crates, traps and barrels around the pier foot and the huts
			double X = PierDims.x;
			void cluster( object[][] items )
			{
				foreach ( var it in items )
				{
					string type = ( string ) it[ 0 ]; double x = Convert.ToDouble( it[ 1 ] ), z = Convert.ToDouble( it[ 2 ] ), ry = Convert.ToDouble( it[ 3 ] );
					double stack = it.Length > 4 ? Convert.ToDouble( it[ 4 ] ) : 0;
					double gy = g( x, z );
					double y = gy + ( stack != 0 ? stack : 0 ) - 0.02;
					var tone = new[] { rand.range( 0.82, 1.08 ), rand.range( 0.84, 1.02 ), rand.range( 0.8, 0.98 ) };
					inst.add( type, x, y, z, ry, tone );
					if ( stack == 0 )
					{
						if ( type == "barrel" ) colliders.addCylinder( x, z, 0.32, gy, gy + 0.9, "barrel" );
						else box( x, gy + 0.25, z, type == "trap" ? 0.48 : 0.33, 0.3, type == "trap" ? 0.28 : 0.24, ry, type );
					}
				}
			}

			cluster( new[]
			{
				new object[] { "barrel", X + 2.4, - 62.4, 0.4 }, new object[] { "barrel", X + 3.05, - 62.9, 1.9 }, new object[] { "crate", X + 2.6, - 61.2, 0.1 },
				new object[] { "crate", X + 2.65, - 61.25, 0.4, 0.4 }, new object[] { "trap", X - 2.6, - 61.0, 0.05 }, new object[] { "trap", X - 2.65, - 61.6, 0.08 }, new object[] { "trap", X - 2.6, - 61.3, 0.2, 0.31 },
				new object[] { "crate", X - 3.4, - 62.4, 0.6 },
				new object[] { "trap", 64.5, - 61.6, 0.3 }, new object[] { "trap", 65.3, - 61.2, 0.15 }, new object[] { "trap", 64.9, - 61.4, 0.5, 0.31 }, new object[] { "trap", 64.6, - 61.5, 1.9, 0.62 },
				new object[] { "barrel", 71.3, - 62.3, 0.1 }, new object[] { "crate", 71.1, - 61.2, 0.3 }, new object[] { "crate", 71.9, - 61.4, 1.3 },
				new object[] { "trap", 83.2, - 67.4, 0.4 }, new object[] { "trap", 83.9, - 67.9, 0.2 }, new object[] { "barrel", 82.6, - 68.9, 0.7 },
				new object[] { "crate", 88.0, - 53.0, 0.2 }, new object[] { "crate", 88.1, - 53.0, 0.6, 0.4 }, new object[] { "barrel", 89.0, - 52.2, 0.3 },
				new object[] { "trap", 34.6, - 68.6, 0.3 }, new object[] { "trap", 35.3, - 68.2, 0.1 }, new object[] { "crate", 39.6, - 68.9, 0.5 },
			} );

			// loose buoys and rope coils on the sand
			var pal = new[] { new[] { C.orange, C.white }, new[] { C.red, C.white }, new[] { C.yellow, C.black }, new[] { C.white, C.blue } };
			foreach ( var p in new[] { new[] { 60.8, - 60.3 }, new[] { 61.3, - 60.9 }, new[] { 66.2, - 58.4 }, new[] { 89.6, - 51.2 }, new[] { 45.2, - 62.3 } } )
			{
				double x = p[ 0 ], z = p[ 1 ];
				var ab = rand.pick( pal );
				int kind = rand.chance( 0.5 ) ? 0 : 2;
				double bseed = rand.next();
				double bry = rand.range( 0, 6 );
				buoy( B, x, g( x, z ) - 0.02, z, ab[ 0 ], ab[ 1 ], kind, bseed, new O { rz = 1.4, ry = bry } );
			}

			ropeCoil( B, 59.6, g( 59.6, - 59.2 ), - 59.2, 0.08, 0.34, 5, rand.next() );
			ropeCoil( B, 90.4, g( 90.4, - 52.4 ), - 52.4, 0.08, 0.28, 4, rand.next(), C.ropeBlue );

			// a buoy string on posts between the huts and the fence-like line of old posts
			for ( int i = 0; i < 5; i ++ )
			{
				double x = 57.8 + i * 1.6, z = - 67.8 - i * 0.25;
				B.cyl( "wood", x, g( x, z ) - 0.4, z, 0.06, 0.07, 1.65, new O { segs = 6, data = WOOD( rand.next(), 0.95 ) } );
				colliders.addCylinder( x, z, 0.08, g( x, z ) - 0.4, g( x, z ) + 1.25, "post" );
				foundationChecks.Add( new FoundationCheck { x = x, y = g( x, z ) - 0.4, z = z } );
				if ( i > 0 )
				{
					double px = 57.8 + ( i - 1 ) * 1.6, pz = - 67.8 - ( i - 1 ) * 0.25;
					B.tube( "rope", new[] { new Vector3( px, g( px, pz ) + 1.15, pz ), new Vector3( ( px + x ) / 2, ( g( px, pz ) + g( x, z ) ) / 2 + 0.95, ( pz + z ) / 2 ), new Vector3( x, g( x, z ) + 1.15, z ) }, 0.014, new O { radial = 4, tint = C.rope, data = new[] { rand.next(), 0, 0, 0 } } );
				}
			}
		}

		// ------------------------------------------------------------------ village props

		void _villageProps( BuildCtx ctx )
		{
			var B = ctx.B; var terrain = ctx.terrain; var colliders = ctx.colliders; var rand = ctx.rand; var lights = ctx.lights;
			double g( double x, double z ) => terrain.HeightAt( x, z );

			// picket fence around house A's side yard and a rail fence near G
			void fenceAt( double[][] pts, string style, double[] tint = null )
			{
				fence( B, pts, g, style, tint, rand.next(), colliders, ( x, z ) => new Vector3( x, 0, z ) );
				foreach ( var p in pts ) foundationChecks.Add( new FoundationCheck { x = p[ 0 ], y = g( p[ 0 ], p[ 1 ] ) - 0.13, z = p[ 1 ] } );
			}

			fenceAt( new[] { new[] { 5.2, - 101.5 }, new[] { 5.6, - 108.5 }, new[] { 9.0, - 111.2 } }, "picket", TRIMS[ "white" ] );
			fenceAt( new[] { new[] { 21.5, - 124.0 }, new[] { 23.6, - 131.5 }, new[] { 22.8, - 135.5 } }, "rail" );
			fenceAt( new[] { new[] { 70.0, - 102.5 }, new[] { 72.5, - 106.0 } }, "picket", TRIMS[ "cream" ] );
			fenceAt( new[] { new[] { 86.5, - 150.0 }, new[] { 87.8, - 158.5 } }, "rail" );

			// laundry lines
			var cloth = new[] { lin( 0xf2f0ea ), lin( 0xd9534a ), lin( 0x4f8fc0 ), lin( 0xf0c850 ), lin( 0x7fbf9f ), lin( 0xe89ab0 ) };
			double[] pole( double x, double z )
			{
				B.cyl( "wood", x, g( x, z ) - 0.3, z, 0.04, 0.05, 2.5, new O { segs = 6, data = WOOD( rand.next(), 0.9 ) } );
				colliders.addCylinder( x, z, 0.06, g( x, z ) - 0.3, g( x, z ) + 2.2, "pole" );
				foundationChecks.Add( new FoundationCheck { x = x, y = g( x, z ) - 0.3, z = z } );
				return new[] { x, g( x, z ) + 2.1, z };
			}

			{ var a = pole( 7.8, - 112.8 ); var b = pole( 12.6, - 114.6 ); laundryLine( B, a, b, rand, cloth ); }
			{ var a = pole( 21.0, - 136.8 ); var b = pole( 25.2, - 137.6 ); laundryLine( B, a, b, rand, cloth ); }
			{ var a = pole( 83.2, - 138.2 ); var b = pole( 87.4, - 140.0 ); laundryLine( B, a, b, rand, cloth ); }

			// fish drying rack and scattered gear in the village
			fishRack( B, 67.0, g( 67.0, - 118.5 ), - 118.5, - 0.25, 2.4, rand.next(), rand );
			colliders.addBox( new Vector3( 67.0, g( 67.0, - 118.5 ) + 1.0, - 118.5 ), new Vector3( 1.35, 1.0, 0.75 ), - 0.25, false, true, "rack" );
			foundationChecks.Add( new FoundationCheck { x = 67.0, y = g( 67.0, - 118.5 ) - 0.2, z = - 118.5 } );
			netRack( B, 5.5, g( 5.5, - 136.5 ), - 136.5, 0.35, 3.0, lin( 0x6a5a8a ), rand.next(), ( lx, lz ) => g( 5.5 + lx * Math.Cos( 0.35 ) + lz * Math.Sin( 0.35 ), - 136.5 - lx * Math.Sin( 0.35 ) + lz * Math.Cos( 0.35 ) ) );
			foreach ( double sx in new[] { - 1.5, 1.5 } )
			{
				double px = 5.5 + sx * Math.Cos( 0.35 ), pz = - 136.5 - sx * Math.Sin( 0.35 );
				colliders.addCylinder( px, pz, 0.1, g( px, pz ) - 0.4, g( px, pz ) + 2.0, "rack" );
				foundationChecks.Add( new FoundationCheck { x = px, y = g( px, pz ) - 0.4, z = pz } );
			}

			var clutter = new[]
			{
				new object[] { "barrel", 20.4, - 105.2, 0.3 }, new object[] { "crate", 21.2, - 104.6, 0.8 }, new object[] { "crate", 21.25, - 104.65, 1.1, 0.4 },
				new object[] { "trap", 69.2, - 110.8, 0.2 }, new object[] { "trap", 69.3, - 111.4, 0.1 }, new object[] { "trap", 69.25, - 111.1, 0.3, 0.31 },
				new object[] { "barrel", 86.6, - 118.8, 0.2 }, new object[] { "crate", 85.9, - 119.6, 0.4 },
				new object[] { "crate", 64.6, - 134.2, 0.3 }, new object[] { "barrel", 63.9, - 133.4, 1.0 },
				new object[] { "trap", 47.9, - 150.2, 0.6 }, new object[] { "trap", 48.2, - 150.8, 0.4 },
				new object[] { "barrel", 25.4, - 157.4, 0.5 }, new object[] { "barrel", 26.1, - 157.8, 1.5 },
				new object[] { "crate", 9.8, - 145.4, 0.2 }, new object[] { "crate", 91.6, - 128.5, 0.9 },
			};
			foreach ( var it in clutter )
			{
				string type = ( string ) it[ 0 ]; double x = Convert.ToDouble( it[ 1 ] ), z = Convert.ToDouble( it[ 2 ] ), ry = Convert.ToDouble( it[ 3 ] );
				double stack = it.Length > 4 ? Convert.ToDouble( it[ 4 ] ) : 0;
				double gy = g( x, z );
				inst.add( type, x, gy + stack - 0.02, z, ry, new[] { rand.range( 0.82, 1.08 ), rand.range( 0.84, 1.02 ), rand.range( 0.8, 0.98 ) } );
				if ( stack == 0 )
				{
					if ( type == "barrel" ) colliders.addCylinder( x, z, 0.32, gy, gy + 0.9, "barrel" );
					else colliders.addBox( new Vector3( x, gy + 0.25, z ), new Vector3( type == "trap" ? 0.48 : 0.33, 0.3, type == "trap" ? 0.28 : 0.24 ), ry, false, true, type );
				}
			}

			// upturned rowboat on trestles in a yard + a bucket
			rowboat( B, 88.2, g( 88.2, - 124.5 ) + 0.55, - 124.5, 1.2, new RowboatO { upsideDown = true, seed = rand.next(), hull = lin( 0xc9463a ), bottom = lin( 0x2d2d2d ), trim = C.white } );
			foreach ( double o in new[] { - 1.2, 1.2 } )
			{
				double px = 88.2 + o * Math.Sin( 1.2 ), pz = - 124.5 + o * Math.Cos( 1.2 );
				B.box( "wood", px, g( px, pz ) + 0.26, pz, 1.1, 0.07, 0.09, new O { grain = 0, ry = 1.2 + Math.PI / 2, data = WOOD( rand.next(), 0.85 ) } );
				foreach ( double s in new[] { - 0.4, 0.4 } )
				{
					double lx = px + s * Math.Cos( 1.2 + Math.PI / 2 ), lz = pz - s * Math.Sin( 1.2 + Math.PI / 2 );
					B.box( "wood", lx, g( lx, lz ) + 0.08, lz, 0.07, 0.5, 0.07, new O { grain = 1, data = WOOD( rand.next(), 0.85 ) } );
				}
			}

			colliders.addBox( new Vector3( 88.2, g( 88.2, - 124.5 ) + 0.6, - 124.5 ), new Vector3( 0.75, 0.6, 2.0 ), 1.2, false, true, "rowboat" );
			foundationChecks.Add( new FoundationCheck { x = 88.2, y = g( 88.2, - 124.5 ) - 0.17, z = - 124.5 } );
			bucket( B, 86.9, g( 86.9, - 122.6 ), - 122.6, C.blue, rand.next() );

			// a couple of extra path lights toward the upper houses
			foreach ( var p in new[] { new[] { 33.5, - 125.5 }, new[] { 48.0, - 130.5 }, new[] { 42.0, - 142.0 } } )
			{
				double x = p[ 0 ], z = p[ 1 ];
				var w = pathLight( B, x, g( x, z ) - 0.25, z, rand.next() );
				lights.Add( new LightSource { position = w, color = new Color( 1.0, 0.7, 0.4 ), intensity = 2.5, kind = "pathLight" } );
				colliders.addCylinder( x, z, 0.1, g( x, z ) - 0.25, g( x, z ) + 0.95, "pathLight" );
				foundationChecks.Add( new FoundationCheck { x = x, y = g( x, z ) - 0.25, z = z } );
			}
		}

		// ------------------------------------------------------------------ meshes

		void _assemble()
		{
			Batch take( string key ) { if ( ! B.batches.TryGetValue( key, out var b ) ) return null; B.batches.Remove( key ); return b; }

			// fold small emitter keys into bigger materials (fewer draw calls):
			//   glass -> wood (pattern 9), rope -> hard (vdata.w = 2 + radius), cloth / net / flag -> fabric
			var wood = B.batch( "wood" ); var hard = B.batch( "hard" ); var fab = B.batch( "fabric" );
			var glass = take( "glass" ); var rope = take( "rope" ); var cloth = take( "cloth" ); var net = take( "net" ); var flag = take( "flag" );
			if ( glass != null ) wood.append( glass, d => new[] { d[ 0 ], d[ 1 ], 9, d[ 2 ] } );
			if ( rope != null ) hard.append( rope, d => new[] { d[ 0 ], 0, 0, 2 + d[ 1 ] } );
			if ( cloth != null ) fab.append( cloth, d => new[] { d[ 0 ], d[ 1 ], 0, 0 } );
			// nets get their own blended material (see createNetMaterial)
			var netBatch = new Batch();
			if ( net != null ) netBatch.append( net, d => new[] { d[ 0 ], d[ 1 ], Math.Max( 0.02, d[ 2 ] ), 0 } );
			if ( flag != null ) fab.append( flag, d => new[] { d[ 0 ], d[ 1 ], d[ 2 ] + 10000, d[ 3 ] } );

			// All opaque geometry lives in ONE set of vertex / index buffers. Each opaque material
			// draws its own index range; the wood mesh is the only shadow caster and widens its
			// range to cover everything during the shadow passes (1 shadow draw per cascade).
			var opaque = new Batch();
			foreach ( var key in new[] { "wood", "hard", "roofMetal", "thatch", "stone" } )
			{
				if ( ! B.batches.TryGetValue( key, out var b ) || b.vcount == 0 ) continue;
				int start = opaque.idx.Count;
				opaque.append( b );
				ranges.Add( new OpaqueRange { key = key, start = start, count = opaque.idx.Count - start } );
			}

			shared = opaque.build();
			shadowTriangles = shared.index.Length / 3;
			if ( fab.vcount > 0 ) fabric = fab.build();
			if ( netBatch.vcount > 0 ) nets = netBatch.build();

			// the fish props are drawn by FishPropsView (the JS adds B.fishProps.build() as one more mesh of the village)
			fishProps = B.fishProps;

			// release the CPU-side builders
			B = harbor = town = null;
			inst = null;
		}

		// The painted fish sign hangs from the arch beam on two short chains: a pendulum about the
		// chain tops (info.signPivot), same materials as the rest of the village (no new pipelines).
		void _buildSign()
		{
			var S = signB; var pivot = pierInfo.signPivot;
			signB = null;
			if ( S == null || pivot == null ) return;
			sign = new SwingPart { pivot = pivot };
			foreach ( var key in new[] { "wood", "hard" } )
			{
				if ( ! S.batches.TryGetValue( key, out var b ) || b.vcount == 0 ) continue;
				var geo = b.build();
				geo.translate( - pivot.x, - pivot.y, - pivot.z );
				sign.geometry[ key ] = geo;
			}

			_swing = new Swing();
		}

		// The pier's lanterns hang from their lamp-post arms (and the arch beam) on short chains: small
		// pendulums about the top of the chain, pushed by the wind with gusts. The light positions follow the lanterns.
		void _buildLanterns()
		{
			var rnd = new System.Random( 4711 ); // JS: Math.random() phases (any value works)
			foreach ( var h in pierInfo.hung )
			{
				var S = h.B;
				h.B = null;
				var part = new SwingPart { pivot = h.pivot };
				S.batches.TryGetValue( "glass", out var glass );
				S.batches.Remove( "glass" );
				if ( glass != null ) S.batch( "wood" ).append( glass, d => new[] { d[ 0 ], d[ 1 ], 9, d[ 2 ] } );
				foreach ( var key in new[] { "wood", "hard" } )
				{
					if ( ! S.batches.TryGetValue( key, out var b ) || b.vcount == 0 ) continue;
					var geo = b.build();
					geo.translate( - h.pivot.x, - h.pivot.y, - h.pivot.z );
					part.geometry[ key ] = geo;
				}

				lanterns.Add( new LanternState { part = part, pivot = h.pivot, rest = h.rest, live = h.live, t = rnd.NextDouble() * 50, ph = rnd.NextDouble() * 6.28 } );
			}
		}

		// ------------------------------------------------------------------ wind animation

		sealed class Swing { public double t, a, av, b, bv; }
		Swing _swing;
		static readonly Euler _lanternEuler = new Euler();

		// current rotation of the sign (Euler 'YXZ': x = swing, y = twist) and of each lantern (x, z)
		public double signRotX => _swing != null ? _swing.a : 0;
		public double signRotY => _swing != null ? _swing.b : 0;

		void _updateLanterns( double dt, double v, double wx, double wz )
		{
			if ( lanterns.Count == 0 ) return;
			int n = ( int ) Math.Max( 1, Math.Ceiling( Math.Min( dt, 0.1 ) / ( 1.0 / 120 ) ) ); double h = Math.Min( dt, 0.1 ) / n;
			double W = 5.4, Z = 0.05; // rad/s (0.34 m pendulum), damping ratio
			foreach ( var l in lanterns )
			{
				for ( int i = 0; i < n; i ++ )
				{
					double t = l.t += h, p = l.ph;
					double gust = 1 + 0.4 * Math.Sin( t * 0.73 + p ) * Math.Sin( t * 0.31 + p * 0.5 ) + 0.25 * Math.Sin( t * 2.3 + Math.Sin( t * 0.9 + p ) * 1.5 );
					// lean the wind holds (tan ~ drag / weight, ~3 degrees at 7 m/s) plus buffeting
					double px = 0.0011 * v * v * gust * wx + 0.0012 * v * Math.Sin( t * 1.9 + p + Math.Sin( t * 0.53 ) * 2 );
					double pz = 0.0011 * v * v * gust * wz + 0.0012 * v * Math.Sin( t * 1.6 + p * 1.7 + Math.Sin( t * 0.41 ) * 2 );
					l.vx += ( W * W * ( px - l.x ) - 2 * Z * W * l.vx ) * h;
					l.x += l.vx * h;
					l.vz += ( W * W * ( pz - l.z ) - 2 * Z * W * l.vz ) * h;
					l.z += l.vz * h;
				}

				// bottom toward +x: +rotation about z; toward +z: -rotation about x
				_lanternEuler.set( l.z, 0, - l.x );
				l.live.copy( l.rest ).sub( l.pivot ).applyEuler( _lanternEuler ).add( l.pivot );
			}
		}

		// wind: speed (m/s) and direction (x, z of the unit wind vector); the lantern lean is l.x / l.z (about z: -x, about x: z)
		public void update( double dt, double windSpeed, double windDirX, double windDirZ )
		{
			_updateLanterns( dt, windSpeed, windDirX, windDirZ );

			// all other animation (flags, nets, laundry, lantern flicker) runs on the GPU
			var s = _swing;
			if ( s == null ) return;
			// damped pendulum driven by the wind's push on the board (drag ~ v^2 on the face-on part of the
			// wind, with gusts), and a stiffer, smaller twist on the two chains
			double v = windSpeed, wx = windDirX, wz = windDirZ;
			int n = ( int ) Math.Max( 1, Math.Ceiling( Math.Min( dt, 0.1 ) / ( 1.0 / 120 ) ) ); double h = Math.Min( dt, 0.1 ) / n;
			double W = 4.9, WT = 10.5; // rad/s: swing (0.4 m to the board's centre) and twist (bifilar chains)
			for ( int i = 0; i < n; i ++ )
			{
				double t = s.t += h;
				double gust = 1 + 0.35 * Math.Sin( t * 0.73 + 1.3 ) * Math.Sin( t * 0.31 ) + 0.22 * Math.Sin( t * 2.1 + Math.Sin( t * 0.9 ) ) + 0.1 * Math.Sin( t * 5.3 + 2.0 );
				// tan of the lean the wind holds the board at (~9 degrees face-on at 7 m/s), plus turbulence
				// that keeps it moving when the wind is along the board
				double push = - 0.0033 * v * v * gust * wz * Math.Abs( wz ) - 0.0009 * v * Math.Sin( t * 1.7 + Math.Sin( t * 0.43 ) * 2 );
				s.av += ( W * W * ( push * Math.Cos( s.a ) - Math.Sin( s.a ) ) - 2 * 0.07 * W * s.av ) * h;
				s.a += s.av * h;
				double twist = 0.004 * v * v * wx * wz * gust + 0.0015 * v * Math.Sin( t * 2.9 + 1.1 );
				s.bv += ( WT * WT * ( twist - s.b ) - 2 * 0.12 * WT * s.bv ) * h;
				s.b += s.bv * h;
			}
		}

		// circles { x, z, r } covered by buildings / big props (useful to keep vegetation out)
		public List<Footprint> getFootprints() => footprints;
	}
}
