using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.Player;
using UnityEngine;
using Vector3 = Tidewater.Engine.Vector3;

// Port of src/world/boats/Pelagic30.js: the Pelagic 30, an offshore centre console (a factor-based glTF asset, public/models/boats/pelagic_30.glb)
// moored next to the lobster boat. The geometry is the one the browser builds from the file (node transforms baked in, the 515 primitives merged
// into one mesh per glTF material, the hull / deck / engines below the T-top as the water-exclusion mask), dumped by unity/tools/dump-pelagic.mjs
// into Resources/pelagic-30.bytes; this class reads it and carries the physics adapter (everything BoatController and the Player read), line for
// line the constructor of the JS class, with the `lines` facade the deck walking reads (here DeckLines) and the axis-aligned deck colliders.
//
// Boat frame as BoatModel: +Z forward, +Y up, +X port; y = 0 the design waterline.
namespace Tidewater.World.Boat
{
	public sealed class Pelagic30 : BoatModel
	{
		public const string RESOURCE = "pelagic-30";
		// the water-exclusion mask turns off inside this box (boat frame; JS Box3( -1.7, -0.85, -5.45 ) .. ( 1.7, 0.3, 5.4 )): only the
		// below-waterline volume, so standing at the helm or on the pier must not switch the mask off
		public static readonly Bounds MASK_CULL_BOX = new Bounds( new UnityEngine.Vector3( 0, - 0.275f, - 0.025f ), new UnityEngine.Vector3( 3.4f, 1.15f, 10.85f ) );

		public readonly List<FactorMaterial> materials = new List<FactorMaterial>();
		public BufferGeometry maskGeometry;
		public Vector3 helmPoint;
		public double[][] stations;
		public double lateralY, hullLift, rudderLift, maxThrust, pitchSpeed, reverseFactor, bowZ, sternZ;
		public Vector3[] contactPoints, outline;

		static Vector3 V( double x, double y, double z ) => new Vector3( x, y, z );

		public Pelagic30() : base( true )
		{
			// ---- physics adapter: measured from the loaded geometry (see the mooring note in WorldLayout): 12 waterplane patches (2 lateral x 6
			// longitudinal) from slicing the hull, anchors from the material-bucket bounds. Patch y is the per-station draft, calibrated so ~2.9 t of
			// displacement floats the origin on the design waterline (the semantics of HullLines.buildHullSamples).
			hullSamples = new List<HullSample>();
			double[][] HS =
			{
				new[] { 0.83, - 0.21, - 4.51, 2.27, - 0.76 }, new[] { 0.9, - 0.12, - 2.73, 2.47, - 0.45 }, new[] { 0.91, - 0.12, - 0.94, 2.5, - 0.45 },
				new[] { 0.91, - 0.12, 0.84, 2.5, - 0.42 }, new[] { 0.82, - 0.07, 2.62, 2.25, - 0.27 }, new[] { 0.41, 0, 4.41, 1.14, 0.31 },
			};
			foreach ( var h in HS )
				foreach ( double sd in new double[] { - 1, 1 } ) hullSamples.Add( new HullSample { position = V( sd * h[ 0 ], h[ 1 ], h[ 2 ] ), area = h[ 3 ], bottomY = h[ 4 ] } );
			// ~2.9 t with twin outboards aft: CoM a little astern of midship, gear high under the T-top
			hydro = new BoatHydro { suggestedMass = 2900, centerOfMass = V( 0, 0.55, - 0.45 ), inertia = V( 13000, 16000, 3600 ) };
			propeller = V( 0, - 0.3, - 5.3 ); // twin outboards on the transom, one thrust point
			rudder = V( 0, 0, - 4.9 );        // outboard steering, approximated with the rudder-force model
			maxThrust = 20000;
			pitchSpeed = 17;
			reverseFactor = 0.3; // outboards astern
			stations = new[] { new[] { - 4.4, 0.7 }, new[] { - 2.8, 0.8 }, new[] { - 1.2, 0.85 }, new[] { 0.5, 0.85 }, new[] { 2.2, 0.6 }, new[] { 3.6, 0.2 } }; // (z, lateral area)
			lateralY = 0.1;
			hullLift = 0.65;  // more drift drag than the full-keel lobster boat: speed bleeds off in turns
			rudderLift = 1.7; // outboards deflect less than a hull-mounted rudder in the race
			contactPoints = new[]
			{
				V( 0, - 0.75, 3.4 ), V( 0, - 0.8, 0 ), V( 0, - 0.75, - 3.6 ),
				V( 1.2, - 0.4, 1.6 ), V( - 1.2, - 0.4, 1.6 ), V( 1.3, - 0.35, - 2.8 ), V( - 1.3, - 0.35, - 2.8 ),
				V( 0, 0.2, 4.6 ),
			};
			outline = new[]
			{
				V( 0, 0.3, 4.8 ), V( 1.4, 0.3, 2.4 ), V( - 1.4, 0.3, 2.4 ),
				V( 1.6, 0.3, - 1.2 ), V( - 1.6, 0.3, - 1.2 ), V( 1.4, 0.3, - 4.2 ), V( - 1.4, 0.3, - 4.2 ),
			};
			bowZ = 5.2;
			sternZ = - 5.1;
			// helm: standing at the wheel on the console deck (chart screen at z ~ -0.58)
			helmEye = V( 0, 2.45, - 1.35 );
			helmPoint = V( 0.4, 1.2, - 1.35 );
			helmPointX = helmPoint.x; helmPointZ = helmPoint.z; // where the deck-walker takes the helm
			boardPoint = V( 0, 0.78, - 4.4 ); // on the aft cockpit sole, inside the transom bench
			exitPoints = new List<Vector3> { V( - 1.5, 0.9, 0.3 ), V( 1.5, 0.9, 0.3 ) }; // both rails, midship

			// ---- deck walking: the `lines` facade (Player.updateDeck contract) + axis-aligned deck colliders, from the measured layout: teak sole y 0.70
			// aft (z -4.6..-2.5) and forward (1.4..4.0); the console deck spans the full beam between them (y 0.90, z -2.5..1.4) and is stepped up
			// onto; the windshield box (x +-0.55) blocks its middle, passed on the open sides.
			var d = new DeckLines { deckY = 0.7, shell = 0.07, zAft = - 4.6, zFwd = 4.0 };
			d.tAtSheerZ = z => Math.Min( 1, Math.Max( 0, ( z - d.zAft ) / ( d.zFwd.Value - d.zAft ) ) );
			// half beam at hull parameter t and height y: piecewise-linear over the measured topside outline (max |x| below the rail), inset ~3%,
			// slight flare with height
			double[][] BEAM =
			{
				new[] { - 5.4, 0.7 }, new[] { - 5.1, 0.9 }, new[] { - 4.85, 1.36 }, new[] { - 4.6, 1.42 }, new[] { - 4.0, 1.38 }, new[] { - 3.4, 1.4 }, new[] { - 2.8, 1.44 },
				new[] { - 2.2, 1.48 }, new[] { - 1.6, 1.51 }, new[] { - 1.0, 1.52 }, new[] { - 0.4, 1.52 }, new[] { 0.2, 1.52 }, new[] { 0.8, 1.5 }, new[] { 1.4, 1.44 },
				new[] { 1.9, 1.2 }, new[] { 2.4, 1.07 }, new[] { 2.9, 0.9 }, new[] { 3.4, 0.7 }, new[] { 3.9, 0.34 }, new[] { 4.15, 0.05 },
			};
			d.halfBreadth = ( t, y ) =>
			{
				double z = d.zAft + t * ( d.zFwd.Value - d.zAft );
				double b0 = BEAM[ 0 ][ 1 ], z0 = BEAM[ 0 ][ 0 ];
				foreach ( var bb in BEAM )
				{
					double bz = bb[ 0 ], bw = bb[ 1 ];
					if ( bz >= z ) return ( b0 + ( bw - b0 ) * ( ( z - z0 ) / Math.Max( 1e-9, bz - z0 ) ) ) * 0.97 * ( 1 + 0.06 * Math.Max( 0, y - d.deckY ) );
					b0 = bw; z0 = bz;
				}

				return b0 * 0.97;
			};
			deck = d;
			colliders = new List<BoatCollider>
			{
				// the console deck: step up onto it (0.2 m), stand at the helm
				Box( - 1.3, 0.7, - 2.5, 1.3, 0.9, 1.4, true, false ),
				// windshield box around the helm: solid, passed on the open sides of the console deck
				Box( - 0.55, 0.9, - 0.75, 0.55, 1.8, 0.7 ),
				// helm seat pedestal (the ring aft of the wheel)
				Box( - 0.5, 0.9, 0.05, 0.5, 1.35, 0.55 ),
				// aft cockpit side cushions and transom bench (solid; tops too high to step)
				Box( 0.9, 0.7, - 4.0, 1.45, 1.08, - 2.5 ),
				Box( - 1.45, 0.7, - 4.0, - 0.9, 1.08, - 2.5 ),
				Box( - 0.8, 0.7, - 5.45, 0.8, 1.4, - 4.95 ),
				// forward cockpit cushions, port and starboard (centre teak walkway stays open)
				Box( 0.7, 0.7, 1.5, 1.4, 1.5, 3.3 ),
				Box( - 1.4, 0.7, 1.5, - 0.7, 1.5, 3.3 ),
			};

			LoadGeometry();
		}

		static BoatCollider Box( double x0, double y0, double z0, double x1, double y1, double z1, bool walkable = false, bool solid = true ) => new BoatCollider
		{
			center = V( ( x0 + x1 ) / 2, ( y0 + y1 ) / 2, ( z0 + z1 ) / 2 ),
			half = V( Math.Abs( x1 - x0 ) / 2, Math.Abs( y1 - y0 ) / 2, Math.Abs( z1 - z0 ) / 2 ),
			walkable = walkable, solid = solid,
		};

		// what BoatController reads of the model: the facade stands in for the hull lines (the topsides' reserve buoyancy, the deck the chock stands on)
		public override Tidewater.Player.BoatDynamics dynamics() => new Tidewater.Player.BoatDynamics
		{
			hydro = hydro, hullSamples = hullSamples, propeller = propeller, rudderZ = rudder.z,
			hasLines = true, zAft = deck.zAft, zFwd = deck.zFwd.Value, deckY = deck.deckY, halfBreadth = deck.halfBreadth, tAtSheerZ = deck.tAtSheerZ,
			stations = stations, lateralY = lateralY, hullLift = hullLift, rudderLift = rudderLift, maxThrust = maxThrust, pitchSpeed = pitchSpeed,
			reverseFactor = reverseFactor, bowZ = bowZ, sternZ = sternZ, contactPoints = contactPoints, outline = outline,
		};

		// the water-exclusion mask is the boat's real surface (hull, deck, engines; everything below the T-top)
		public override BufferGeometry createHullVolumeGeometry() => maskGeometry;

		// Resources/pelagic-30.bytes (FactorBoatAsset)
		void LoadGeometry() { maskGeometry = FactorBoatAsset.Load( RESOURCE, materials, "dump-pelagic.mjs" ); }
	}
}
