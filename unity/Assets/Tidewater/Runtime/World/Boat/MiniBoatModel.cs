using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tidewater.Engine;
using Tidewater.Player;
using UnityEngine;
using Vector3 = Tidewater.Engine.Vector3;

// Port of src/world/boats/MiniFishingBoat.js: the mini fishing boat, a 3 m one-man punt (a glTF asset, public/models/boats/mini_fishing_boat.glb).
// The geometry is the one the browser builds from the file (node transforms baked in, the studio extras left out, one merged mesh per glTF
// material, the hull shell and sole as the water-exclusion mask), dumped by unity/tools/dump-mini-boat.mjs into
// Resources/mini-fishing-boat.bytes; this class reads it and carries the physics adapter (the numbers BoatController and the Player read),
// line for line the constructor of the JS class. No hull lines and no deck to walk: boarding goes straight to the helm (the forward seat).
//
// Boat frame as BoatModel: +Z forward, +Y up, +X port; y = 0 the design waterline.
namespace Tidewater.World.Boat
{
	public sealed class FactorMaterial
	{
		public string name; public double[] color, emissive; public double roughness, metalness, clearcoat, clearcoatRoughness;
		public BufferGeometry geometry;
	}

	public sealed class MiniBoatModel : BoatModel
	{
		public const string RESOURCE = "mini-fishing-boat";
		// the water-exclusion mask turns off inside this box (boat frame, Unity axes: x mirrored but symmetric): only the below-waterline volume
		// (the angler sits above it)
		public static readonly Bounds MASK_CULL_BOX = new Bounds( new UnityEngine.Vector3( 0, - 0.25f, 0 ), new UnityEngine.Vector3( 1.6f, 0.7f, 3.8f ) );

		public readonly List<FactorMaterial> materials = new List<FactorMaterial>();
		public BufferGeometry maskGeometry;
		public Vector3 helmPoint;
		public double forceScale = 0.12; // the controller's damping / resistance / mooring constants are for a 3 t boat
		public readonly List<HullSample> reserveSamples = new List<HullSample>();
		public double[][] stations;
		public double lateralY, hullLift, rudderLift, maxThrust, pitchSpeed, reverseFactor, bowZ, sternZ, chockY;
		public Vector3[] contactPoints, outline;

		[Serializable] class Block { public int vertices, indices; public long offset; }
		[Serializable] class MatBlock : Block { public string name; public double[] color, emissive; public double roughness, metalness, clearcoat, clearcoatRoughness; }
		[Serializable] class Header { public MatBlock[] materials; public Block mask; }

		static Vector3 V( double x, double y, double z ) => new Vector3( x, y, z );

		public MiniBoatModel() : base( true )
		{
			// ---- physics adapter: measured from the hull of the file (see MiniFishingBoat.js): 12 waterplane patches (2 lateral x 6 longitudinal, 0.49 m
			// long, 0.97 of the plan area, the lateral position at 0.75 of the half breadth) and the draft per station from the hull bottom
			double[][] ST = { new[] { 1.22, 0.45 }, new[] { 0.73, 0.555 }, new[] { 0.23, 0.56 }, new[] { - 0.27, 0.56 }, new[] { - 0.76, 0.545 }, new[] { - 1.25, 0.5 } };
			hullSamples = new List<HullSample>();
			foreach ( var st in ST )
				foreach ( double sd in new double[] { - 1, 1 } )
					hullSamples.Add( new HullSample { position = V( sd * st[ 1 ] * 0.75, - 0.095, st[ 0 ] ), area = st[ 1 ] * 0.49 * 0.97, bottomY = - 0.11 } );
			hydro = new BoatHydro
			{
				suggestedMass = 330,
				centerOfMass = V( 0, 0.32, 0.05 ), // the angler sits a little forward of the middle, the battery aft and low
				inertia = V( 250, 270, 85 ),       // pitch, yaw, roll: 0.26 L, 0.27 L, 0.36 B radii of gyration
			};
			// reserve buoyancy of the flared topsides: pushes only once the water reaches the rail
			foreach ( double z in new[] { - 0.9, - 0.45, 0, 0.45, 0.9 } )
				foreach ( double sd in new double[] { - 1, 1 } ) reserveSamples.Add( new HullSample { position = V( sd * 0.62, 0.12, z ), area = 0.35 } );
			propeller = V( 0, - 0.12, - 1.5 ); // the aft drive, one thrust point under the stern
			rudder = V( 0, 0, - 1.45 );        // the drive turns: approximated with the rudder-force model
			maxThrust = 700;                   // N bollard pull of a 55 lb thrust / pedal drive plus the trolling motor
			pitchSpeed = 6.5;                  // m/s: thrust falls to nothing there, so ~4.5 m/s flat out
			reverseFactor = 0.55;
			stations = new[] { new[] { - 1.3, 0.16 }, new[] { - 0.6, 0.2 }, new[] { 0.1, 0.2 }, new[] { 0.8, 0.18 }, new[] { 1.3, 0.08 } }; // (z, lateral area m^2)
			lateralY = - 0.02;
			hullLift = 0.15; // flat bottom and no keel: it slides in turns
			rudderLift = 5;
			contactPoints = new[]
			{
				V( 0, - 0.12, 1.3 ), V( 0, - 0.12, 0 ), V( 0, - 0.12, - 1.35 ),
				V( 0.5, - 0.1, 0.6 ), V( - 0.5, - 0.1, 0.6 ), V( 0.5, - 0.1, - 1.2 ), V( - 0.5, - 0.1, - 1.2 ),
				V( 0, 0.1, 1.5 ),
			};
			outline = new[]
			{
				V( 0, 0.2, 1.55 ), V( 0.6, 0.2, 0.9 ), V( - 0.6, 0.2, 0.9 ),
				V( 0.66, 0.2, - 0.5 ), V( - 0.66, 0.2, - 0.5 ), V( 0.6, 0.2, - 1.5 ), V( - 0.6, 0.2, - 1.5 ),
			};
			bowZ = 1.6;
			chockY = 0.45; // the bow eye
			sternZ = - 1.5;
			// helm: the forward seat, facing the bow and the bow control; the player boards over the middle of the boat and sits down there
			helmEye = V( 0, 1.5, 0.62 );
			helmPoint = V( 0, 0.78, 0.62 );
			boardPoint = V( 0, 0.5, 0 );
			exitPoints = new List<Vector3> { V( - 0.65, 0.5, 0.1 ), V( 0.65, 0.5, 0.1 ) }; // both gunwales, amidships
			colliders = new List<BoatCollider>();

			LoadGeometry();
		}

		public override Tidewater.Player.BoatDynamics dynamics() => new Tidewater.Player.BoatDynamics
		{
			hydro = hydro, hullSamples = hullSamples, propeller = propeller, rudderZ = rudder.z, hasLines = false,
			forceScale = forceScale, reserveSamples = reserveSamples, chockY = chockY,
			stations = stations, lateralY = lateralY, hullLift = hullLift, rudderLift = rudderLift, maxThrust = maxThrust, pitchSpeed = pitchSpeed,
			reverseFactor = reverseFactor, bowZ = bowZ, sternZ = sternZ, contactPoints = contactPoints, outline = outline,
		};

		public override BufferGeometry createHullVolumeGeometry() => maskGeometry;

		// Resources/mini-fishing-boat.bytes: uint32 header length, the JSON header, then per block float32 position, normal (3), uv (2), uint32 indices
		void LoadGeometry()
		{
			var asset = Resources.Load<TextAsset>( RESOURCE );
			if ( asset == null ) { UnityEngine.Debug.LogError( "MiniBoatModel: Resources/" + RESOURCE + ".bytes is missing (node unity/tools/dump-mini-boat.mjs)" ); return; }
			var bytes = asset.bytes;
			int headLen = ( int ) BitConverter.ToUInt32( bytes, 0 );
			var head = JsonUtility.FromJson<Header>( Encoding.UTF8.GetString( bytes, 4, headLen ) );
			int data = 4 + headLen;

			BufferGeometry read( Block b )
			{
				int nv = b.vertices, ni = b.indices; long o = data + b.offset;
				var pos = new float[ nv * 3 ]; var nrm = new float[ nv * 3 ]; var uv = new float[ nv * 2 ]; var idx = new int[ ni ];
				Buffer.BlockCopy( bytes, ( int ) o, pos, 0, nv * 12 ); o += nv * 12;
				Buffer.BlockCopy( bytes, ( int ) o, nrm, 0, nv * 12 ); o += nv * 12;
				Buffer.BlockCopy( bytes, ( int ) o, uv, 0, nv * 8 ); o += nv * 8;
				Buffer.BlockCopy( bytes, ( int ) o, idx, 0, ni * 4 );
				var g = new BufferGeometry();
				g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
				g.setAttribute( "normal", new BufferAttribute( nrm, 3 ) );
				g.setAttribute( "uv", new BufferAttribute( uv, 2 ) );
				g.setIndex( idx );
				g.computeBoundingSphere();
				return g;
			}

			foreach ( var m in head.materials )
				materials.Add( new FactorMaterial
				{
					name = m.name, color = m.color, emissive = m.emissive, roughness = m.roughness, metalness = m.metalness,
					clearcoat = m.clearcoat, clearcoatRoughness = m.clearcoatRoughness, geometry = read( m ),
				} );
			maskGeometry = read( head.mask );
		}
	}
}
