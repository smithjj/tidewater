using System;
using System.Collections.Generic;
using System.Linq;
using Tidewater.Engine;
using static Tidewater.World.Boat.HullBuilder;
using static Tidewater.World.Boat.Wheelhouse;

// Port of src/world/BoatModel.js: the ~8.2 m Downeast lobster boat, built procedurally. This is the data of the model (the geometry per
// material bucket, the animated parts' geometry and pivots, dimensions, hydrostatics, anchor points, colliders); BoatView (Unity side)
// turns it into meshes.
//
// Boat frame: +Z forward, +Y up, +X port. y = 0 is the design waterline, z = 0 the middle of the waterline, x = 0 the centerline.
//
// Integration notes:
// - All anchor points (helmEye, boardPoint, exitPoints, propeller, rudder, bowSprayPoints, hullSamples, colliders) are in the boat
//   frame.
// - hullSamples: position.y is the mean hull depth of each sample's waterplane patch, so sum( area * max( 0, waterY - y ) ) * rho * g
//   equals the displaced weight; with mass = hydro.suggestedMass the boat floats on its design waterline. bottomY holds the literal
//   hull bottom under the sample.
// - Put the centre of mass at hydro.centerOfMass (over the centre of buoyancy) for level trim.
// - setSteering( +1 ) turns to port (left): positive yaw rate about +Y.
namespace Tidewater.World.Boat
{
	public sealed class BoatDimensions
	{
		public double length, beam, draft, freeboard, freeboardBow, deckHeight, hullDepth, waterlineLength, waterlineBeam, houseRoofHeight;
	}

	public sealed class BoatHydro
	{
		public double waterplaneArea, canoeVolume, keelVolume, displacedVolume, suggestedMass, massWithKeel, centerOfFlotationZ, waterlineStart, waterlineEnd, seawaterDensity, metacentricRadius;
		public Vector3 centerOfBuoyancy, centerOfMass, inertia;
	}

	public sealed class BoatCollider
	{
		public string tag; public Vector3 center, half; public bool walkable, solid;
	}

	public sealed class BoatModel
	{
		public static readonly string[] STATIC_BUCKETS = { "hull", "gelcoat", "wood", "fittings", "trap", "glow", "glass" };
		public const double WHEEL_TURNS = 0.75;     // wheel turns from centre to hard over
		public const double THROTTLE_ANGLE = 0.6;   // lever travel (rad) from neutral to full
		public const double RADAR_RPM = 24;
		public const double PROP_DISPLAY_RPS = 5;

		public readonly HullLines lines;
		public readonly Dictionary<string, BufferGeometry> staticGeometry = new Dictionary<string, BufferGeometry>();
		public readonly WheelhouseParts parts = new WheelhouseParts();
		// animated parts: geometry and the position of their pivot in the boat frame
		public BufferGeometry wheelGeometry, throttleGeometry, radarGeometry, propGeometry, rudderGeometry;
		public Vector3 wheelPos, throttlePos, radarPos, propPos, rudderPos;
		public Quaternion wheelPivotRotation;
		public BoatDimensions dimensions;
		public BoatHydro hydro;
		public Vector3 helmEye, boardPoint, propeller, rudder;
		public double helmPointX, helmPointZ;
		public List<Vector3> exitPoints = new List<Vector3>(), bowSprayPoints = new List<Vector3>();
		public List<HullSample> hullSamples;
		public List<BoatCollider> colliders;

		static double transverseInertia( HullLines lines )
		{
			double I = 0;
			int n = 800; double z0 = lines.wlStart, dz = ( lines.wlEnd - lines.wlStart ) / n;
			for ( int i = 0; i < n; i ++ ) I += ( 2.0 / 3 ) * Math.Pow( lines.halfBeamAt( z0 + ( i + 0.5 ) * dz ), 3 ) * dz;
			return I;
		}

		public BoatModel()
		{
			lines = new HullLines();
			var kit = new GeoKit();

			buildHull( kit, lines );
			buildWheelhouse( kit, lines, parts );
			DeckGear.buildDeckGear( kit, lines, parts );

			foreach ( var name in STATIC_BUCKETS )
			{
				var geo = kit.merged( name );
				if ( geo == null ) continue;
				staticGeometry[ name ] = geo;
			}

			// ---- animated parts (one mesh each)
			wheelPos = parts.wheelCenter;
			wheelPivotRotation = new Quaternion().setFromUnitVectors( new Vector3( 0, 0, 1 ), parts.wheelAxis );
			wheelGeometry = Wheelhouse.wheelGeometry();
			throttlePos = parts.throttlePivot; throttleGeometry = Wheelhouse.throttleGeometry();
			radarPos = parts.radarPivot; radarGeometry = Wheelhouse.radarArrayGeometry();
			propPos = PROP.position; propGeometry = Running.propellerGeometry();
			rudderPos = RUDDER.pivot; rudderGeometry = Running.rudderGeometry();

			// ---- dimensions and hydrostatics
			double kv = keelVolume( lines );
			double displaced = lines.canoeVolume + kv;
			double maxBeam = 0;
			for ( int i = 0; i <= 200; i ++ ) maxBeam = Math.Max( maxBeam, lines.sheerX( i / 200.0 ) );
			double minSheer = double.PositiveInfinity;
			for ( int i = 0; i <= 200; i ++ ) minSheer = Math.Min( minSheer, lines.sheerY( i / 200.0 ) );

			dimensions = new BoatDimensions
			{
				length = lines.length,                                 // hull LOA (stem head to transom), m
				beam = maxBeam * 2,                                    // max beam at the sheer (rubrail adds ~0.08)
				draft = -KEEL.bottom,                                  // keel shoe below the waterline
				freeboard = minSheer,                                  // lowest sheer height (aft)
				freeboardBow = lines.sheerY( 1 ),
				deckHeight = lines.deckY,
				hullDepth = lines.draftAt( lines.centerOfFlotationZ ), // canoe body depth
				waterlineLength = lines.wlEnd - lines.wlStart,
				waterlineBeam = 2 * maxHalfBeam(),
				houseRoofHeight = roofTopY( 0 ),
			};

			hydro = new BoatHydro
			{
				waterplaneArea = lines.waterplaneArea,                                    // m^2
				canoeVolume = lines.canoeVolume,                                          // m^3, hull below y = 0 (what hullSamples integrate)
				keelVolume = kv,                                                          // m^3, keel/skeg appendage
				displacedVolume = displaced,                                              // m^3
				suggestedMass = JS.Round( HullLines.RHO_SEAWATER * lines.canoeVolume ),   // kg, floats exactly on the design WL with hullSamples
				massWithKeel = JS.Round( HullLines.RHO_SEAWATER * displaced ),
				centerOfBuoyancy = lines.centerOfBuoyancy.clone(),                        // put the centre of mass at this z for level trim
				centerOfFlotationZ = lines.centerOfFlotationZ,
				waterlineStart = lines.wlStart,
				waterlineEnd = lines.wlEnd,
				seawaterDensity = HullLines.RHO_SEAWATER,
			};

			// Suggested rigid-body properties (boat frame). Centre of mass over the centre of buoyancy for level trim, ~0.3 m above the
			// waterline (engine low, wheelhouse high). Inertia from radii of gyration: roll ~0.36 B, pitch/yaw ~0.26 L.
			double mass = hydro.suggestedMass;
			double kRoll = 0.36 * dimensions.beam, kPitch = 0.26 * lines.length, kYaw = 0.27 * lines.length;
			hydro.centerOfMass = new Vector3( 0, 0.3, lines.centerOfBuoyancy.z );
			hydro.inertia = new Vector3( mass * kPitch * kPitch, mass * kYaw * kYaw, mass * kRoll * kRoll ); // about x (pitch), y (yaw), z (roll)
			hydro.metacentricRadius = transverseInertia( lines ) / lines.canoeVolume; // BM (m)

			// ---- anchor points (boat frame)
			helmEye = new Vector3( HOUSE.helmX, 1.85, 0.3 );
			helmPointX = HOUSE.helmX; helmPointZ = HOUSE.seatZ; // where the deck-walker takes the helm
			boardPoint = new Vector3( 0, lines.deckY, -1.75 );

			foreach ( double z in new[] { -3.0, -2.0, -1.2 } )
			{
				double t = lines.tAtSheerZ( z );
				foreach ( double s in new double[] { 1, -1 } ) exitPoints.Add( new Vector3( s * ( lines.sheerX( t ) - 0.035 ), lines.sheerY( t ) + 0.05, z ) );
			}

			propeller = PROP.position.clone();
			rudder = RUDDER.pivot.clone();

			foreach ( double z in new[] { 2.0, 2.6, 3.2, 3.8 } )
			{
				double hb = lines.halfBeamAt( z );
				foreach ( double s in new double[] { 1, -1 } ) bowSprayPoints.Add( new Vector3( s * ( hb + 0.01 ), 0.05, z ) );
			}

			hullSamples = lines.buildHullSamples( 8 );
			colliders = buildColliders();
		}

		// Closed, low-poly hull volume (shell + transom + lid at the sheer) in the boat frame. Not part of the model: useful as a
		// water-exclusion mask (e.g. depth/stencil pre-pass so the ocean surface is not drawn inside the cockpit), occlusion or physics
		// proxies.
		public BufferGeometry createHullVolumeGeometry() => buildHullVolume( lines );

		double maxHalfBeam()
		{
			double m = 0;
			for ( double z = lines.wlStart; z <= lines.wlEnd; z += 0.02 ) m = Math.Max( m, lines.halfBeamAt( z ) );
			return m;
		}

		// what BoatController reads of the model
		public Tidewater.Player.BoatDynamics dynamics() => new Tidewater.Player.BoatDynamics
		{
			hydro = hydro, hullSamples = hullSamples, propeller = propeller, rudderZ = rudder.z,
			hasLines = true, zAft = lines.zAft, zFwd = lines.zBow, deckY = lines.deckY, halfBreadth = lines.halfBreadth, tAtSheerZ = lines.tAtSheerZ,
		};

		public double halfBeamAt( double z ) => lines.halfBeamAt( z );
		public double draftAt( double z ) => lines.draftAt( z );

		public double triangleCount
		{
			get
			{
				double n = 0;
				foreach ( var g in staticGeometry.Values ) n += GK.triangleCount( g );
				foreach ( var g in new[] { wheelGeometry, throttleGeometry, radarGeometry, propGeometry, rudderGeometry } ) n += GK.triangleCount( g );
				return n;
			}
		}

		// Rough boat-frame colliders (axis-aligned in the boat frame) for a character controller.
		List<BoatCollider> buildColliders()
		{
			var L = lines;
			var boxes = new List<BoatCollider>();
			Action<string, Vector3, Vector3, bool, bool> add = ( tag, min, max, walkable, solid ) =>
			{
				var center = new Vector3().addVectors( min, max ).multiplyScalar( 0.5 );
				var half = new Vector3().subVectors( max, min ).multiplyScalar( 0.5 );
				boxes.Add( new BoatCollider { tag = tag, center = center, half = half, walkable = walkable, solid = solid } );
			};

			Func<double, double, double, Vector3> V = ( x, y, z ) => new Vector3( x, y, z );
			Func<double, double> inner = z => L.halfBreadth( L.tAtSheerZ( z ), L.deckY ) - L.shell;
			add( "deck", V( -inner( -1.5 ), L.deckY - 0.1, L.zAft + L.shell ), V( inner( -1.5 ), L.deckY, HOUSE.dash.zFace ), true, false );
			// bulwarks in three segments following the sheer
			foreach ( var zz in new[] { new[] { L.zAft, -2.3 }, new[] { -2.3, -0.7 }, new[] { -0.7, L.houseFront } } )
			{
				double z0 = zz[ 0 ], z1 = zz[ 1 ];
				double t0 = L.tAtSheerZ( z0 ), t1 = L.tAtSheerZ( z1 );
				double top = Math.Max( L.sheerY( t0 ), L.sheerY( t1 ) ) + 0.045;
				double xo = Math.Max( L.sheerX( t0 ), L.sheerX( t1 ) );
				double xi = Math.Min( inner( z0 ), inner( z1 ) );
				foreach ( double s in new double[] { 1, -1 } ) add( "bulwark", V( Math.Min( s * xi, s * xo ), L.deckY, z0 ), V( Math.Max( s * xi, s * xo ), top, z1 ), false, true );
			}

			add( "transom", V( -L.sheerX( 0 ), L.deckY, L.zAft ), V( L.sheerX( 0 ), L.sheerY( 0 ) + 0.045, L.zAft + L.shell ), false, true );
			foreach ( double s in new double[] { 1, -1 } )
			{
				double x0 = houseHalfWidth( L, L.houseBack ) - HOUSE.wallT, x1 = houseHalfWidth( L, L.houseBack );
				add( "houseWall", V( Math.Min( s * x0, s * x1 ), L.sheerY( L.tAtSheerZ( L.houseBack ) ), L.houseBack ), V( Math.Max( s * x0, s * x1 ), HOUSE.roofUnderY, L.houseFront ), false, true );
			}

			add( "console", V( -HOUSE.dash.halfW, L.deckY, HOUSE.dash.zFace ), V( HOUSE.dash.halfW, HOUSE.dash.yTop, L.houseFront ), false, true );
			add( "helmSeat", V( HOUSE.helmX - 0.23, L.deckY, HOUSE.seatZ - 0.24 ), V( HOUSE.helmX + 0.23, 1.05, HOUSE.seatZ + 0.21 ), false, true );
			add( "bench", V( 0.65, L.deckY, 0.075 ), V( 1.07, L.deckY + 0.47, 0.825 ), true, true );
			add( "roof", V( -1.25, HOUSE.roofUnderY, HOUSE.roofZ0 ), V( 1.25, roofTopY( 0 ), HOUSE.roofZ1 ), true, true );
			// (the lobster pots on the deck are the modelled trap, placed on the boat's group by game/Traps.js from the same layout in
			//  world/boat/DeckGear.js: they are not part of the hull's static geometry)

			add( "hauler", V( HAULER.x - 0.2, L.deckY, HAULER.z - 0.08 ), V( HAULER.x + 0.2, HAULER.y + 0.2, HAULER.z + 0.25 ), false, true );
			add( "baitBarrel", V( 0.6, L.deckY, -1.06 ), V( 1.12, L.deckY + 0.8, -0.54 ), false, true );
			// foredeck steps (walkable) from the house front to the stem head
			foreach ( var zz in new[] { new[] { L.houseFront, 2.4 }, new[] { 2.4, 3.3 }, new[] { 3.3, 4.15 } } )
			{
				double z0 = zz[ 0 ], z1 = zz[ 1 ];
				double t0 = L.tAtSheerZ( z0 ), t1 = L.tAtSheerZ( z1 );
				double w = L.sheerX( t0 ) - L.shell;
				add( "foredeck", V( -w, L.deckY, z0 ), V( w, ( L.sheerY( t0 ) + L.sheerY( t1 ) ) / 2 + 0.06, z1 ), true, true );
			}

			return boxes;
		}
	}
}
