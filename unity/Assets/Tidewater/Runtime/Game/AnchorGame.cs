using System;
using System.Collections.Generic;
using Tidewater.Core;
using Tidewater.Engine;
using Tidewater.Player;
using Tidewater.Util;
using Tidewater.World.Boat;
using UnityEngine;
using static Tidewater.World.Boat.GK;
using Quaternion = Tidewater.Engine.Quaternion;

// The anchor, the Unity half of Game.js's `toggleAnchor` / `anchorHint` and of AnchorGear.js. X aboard a boat (at the helm or on deck) lets the anchor go from the bow chock or
// weighs it (the physics is BoatController's, already ported), with the toasts and the splash; driving against a holding anchor says why the boat will not go, once in a while.
// AnchorGear is what an anchored boat shows: a small iron anchor on the seabed where it landed, the line from the bow chock down to it, and an orange buoy on the surface over it.
// Dropping runs the anchor down from the chock and weighing runs it back up. Material: the Boat shader's kind 8 (the game props), the JS `createPropMaterial( 'anchor' )`.
// The meshes are the JS GeoKit builds (boat-frame, so mirrored in x by UnityMesh); positions go through Sim.ToUnity and rotations through Sim.BoatToUnity, as the traps do.
namespace Tidewater.Game
{
	public sealed class AnchorGame
	{
		readonly GameHost game;
		readonly Transform parent;
		readonly IGameAudio audio;
		readonly AnchorGear gear;
		readonly Tidewater.Engine.Vector3 _tmp = new Tidewater.Engine.Vector3();
		double hintT;
		string hintLabel = "X";

		public AnchorGame( GameHost game, PlayerHost host, Transform parent )
		{
			this.game = game; this.parent = parent;
			audio = new Tidewater.Audio.GameAudioProxy( host );
			var ocean = Tidewater.Ocean.OceanRenderer.instance;
			gear = new AnchorGear( parent, host.terrainData.HeightAt, ocean != null ? ocean.query : null );
		}

		public void Dispose() => gear.Dispose();

		// every frame, after the player has been updated
		public void Update( double dt, GameInput inp, Tidewater.Player.Player p )
		{
			if ( ! gear.Alive ) { gear.Dispose(); gear.Rebuild( parent ); }
			if ( inp.actHit( "anchor" ) ) Toggle( p );
			hintLabel = inp.label( "anchor" ) is string l && l != "" ? l : "X";
			Hint( dt, p );
			gear.Update( p.boats, dt );
		}

		// Down or up for the boat the player is aboard. It lands under the bow chock.
		public bool Toggle( Tidewater.Player.Player p )
		{
			if ( p.mode != "boat" && p.mode != "deck" ) return false;
			var b = p.boat;
			if ( b.anchor.down )
			{
				b.weighAnchor();
				game.Toast( "Anchor up", 2.2f );
				return true;
			}

			var c = b.toWorld( b.chock, _tmp );
			double depth = Math.Max( 0, - game.Host.terrainData.HeightAt( c.x, c.z ) );
			var r = b.dropAnchor( depth );
			if ( ! r.ok ) { game.Toast( r.reason, 2.4f ); return false; }
			audio?.splash( 0.6 );
			game.Toast( $"Anchor down · {Math.Round( r.rode ).ToString( "F0", System.Globalization.CultureInfo.InvariantCulture )} m of line", 2.8f );
			return true;
		}

		// driving against the anchor: say why the boat will not go (once in a while, not every frame)
		void Hint( double dt, Tidewater.Player.Player p )
		{
			hintT = Math.Max( 0, hintT - dt );
			if ( p.mode != "boat" || ! p.boat.anchor.down || hintT > 0 ) return;
			if ( p.boat.throttle > 0.4 && p.boat.anchor.tension > 2000 )
			{
				hintT = 12;
				game.Toast( $"The anchor is holding · {hintLabel} to weigh it", 2.6f );
			}
		}
	}

	public sealed class AnchorGear
	{
		const double FALL = 0.9; // s to run the anchor down (or up)

		sealed class View { public GameObject anchor, line, buoy; public double t; public int slot = -1; }

		readonly Func<double, double, double> terrainHeightAt;
		readonly IWaterQuery query;
		readonly int slot = -1;
		readonly Material material;
		readonly UnityEngine.Mesh anchorMesh, lineMesh, buoyMesh;
		readonly Dictionary<BoatController, View> views = new Dictionary<BoatController, View>();
		GameObject root;
		static readonly Tidewater.Engine.Vector3 _c = new Tidewater.Engine.Vector3(), _p = new Tidewater.Engine.Vector3(), _d = new Tidewater.Engine.Vector3(), _Y = new Tidewater.Engine.Vector3( 0, 1, 0 );
		static readonly Quaternion _q = new Quaternion();
		static readonly Euler _e = new Euler();

		public AnchorGear( Transform parent, Func<double, double, double> terrainHeightAt, IWaterQuery query )
		{
			this.terrainHeightAt = terrainHeightAt; this.query = query;
			if ( query != null )
			{
				try { slot = query.Allocate( "anchors", 2 ); }
				catch ( Exception e ) { UnityEngine.Debug.LogWarning( "AnchorGear: no water query slots, the buoys will sit at sea level: " + e.Message ); }
			}

			material = new Material( Shader.Find( "Tidewater/Boat" ) ) { name = "anchor", hideFlags = HideFlags.HideAndDontSave };
			material.SetFloat( "_BoatKind", 8 );
			material.SetFloat( "_CullMode", 2f );
			anchorMesh = Mesh( buildAnchor(), "Anchor" );
			buoyMesh = Mesh( buildBuoy(), "AnchorBuoy" );
			lineMesh = Mesh( prepare( cylinder( 0.035, 0.035, 1, 6 ), new Opts { color = 0xd9cba8, rough = 0.9 } ), "AnchorLine" ); // unit length, scaled to span
			Rebuild( parent );
		}

		static UnityEngine.Mesh Mesh( BufferGeometry g, string name ) { var m = UnityMesh.Create( g, name ); m.hideFlags = HideFlags.DontSave; return m; }

		// false once the Editor has destroyed the objects (entering Play mode drops what the Editor made, while the C# objects survive)
		public bool Alive => root != null;

		public void Rebuild( Transform parent )
		{
			var old = parent != null ? parent.Find( "Anchors" ) : null;
			if ( old != null ) UnityEngine.Object.DestroyImmediate( old.gameObject );
			root = new GameObject( "Anchors" ) { hideFlags = HideFlags.DontSave };
			if ( parent != null ) root.transform.SetParent( parent, false );
			views.Clear();
		}

		GameObject Make( string name, UnityEngine.Mesh mesh )
		{
			var go = new GameObject( name ) { hideFlags = HideFlags.DontSave };
			go.transform.SetParent( root.transform, false );
			go.AddComponent<MeshFilter>().sharedMesh = mesh;
			var mr = go.AddComponent<MeshRenderer>();
			mr.sharedMaterial = material;
			mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
			mr.receiveShadows = true;
			go.SetActive( false );
			return go;
		}

		View ViewOf( BoatController boat )
		{
			if ( ! views.TryGetValue( boat, out var v ) )
			{
				v = new View { anchor = Make( "Anchor", anchorMesh ), line = Make( "AnchorLine", lineMesh ), buoy = Make( "AnchorBuoy", buoyMesh ), slot = slot >= 0 && views.Count < 2 ? slot + views.Count : -1 };
				v.anchor.transform.localScale = UnityEngine.Vector3.one * 1.7f; // oversize (about 1.5 m): it has to read from the surface, against dark seagrass
				views[ boat ] = v;
			}

			return v;
		}

		static UnityEngine.Vector3 U( Tidewater.Engine.Vector3 p ) => Sim.ToUnity( p.x, p.y, p.z );
		static UnityEngine.Quaternion Rot( double x, double y, double z ) => Sim.BoatToUnity( _q.setFromEuler( _e.set( x, y, z ) ) );

		public void Update( IEnumerable<BoatController> boats, double dt )
		{
			foreach ( var boat in boats )
			{
				if ( boat == null || boat.anchor == null ) continue;
				var A = boat.anchor; var v = ViewOf( boat );
				double target = A.down ? 1 : 0, step = Math.Min( dt, 0.1 ) / FALL;
				v.t += Math.Max( - step, Math.Min( step, target - v.t ) );
				bool show = v.t > 0.001;
				v.anchor.SetActive( show ); v.line.SetActive( show );
				// the buoy floats over the anchor once it has landed, until the line starts coming up
				bool buoy = A.down && v.t >= 1;
				v.buoy.SetActive( buoy );
				if ( buoy )
				{
					double y = 0;
					if ( v.slot >= 0 && query != null )
					{
						query.SetPoint( v.slot, ( float ) A.x, ( float ) A.z );
						double h = query.cpu[ v.slot * 4 ];
						if ( ! double.IsNaN( h ) && ! double.IsInfinity( h ) ) y = h;
					}

					v.buoy.transform.SetPositionAndRotation( Sim.ToUnity( A.x, y - 0.06, A.z ), Rot( 0, 0, Math.Sin( A.x * 3.1 + A.z ) * 0.1 ) );
				}

				if ( ! show ) continue;

				// from the chock down to where it lies on the bottom (eased: it gathers speed and settles)
				double e = v.t * v.t * ( 3 - 2 * v.t );
				var chock = boat.toWorld( boat.chock, _c );
				double bed = terrainHeightAt( A.x, A.z ) + 0.1;
				_p.set( chock.x + ( A.x - chock.x ) * e, chock.y + ( bed - chock.y ) * e, chock.z + ( A.z - chock.z ) * e );
				// upright on the way down, over onto its side on the bottom
				v.anchor.transform.SetPositionAndRotation( U( _p ), Rot( 0, A.x * 0.7 + A.z * 0.3, 1.25 * e * e ) );
				_d.subVectors( _p, chock );
				double len = Math.Max( 0.05, _d.length() );
				_c.set( chock.x + _d.x * 0.5, chock.y + _d.y * 0.5, chock.z + _d.z * 0.5 );
				v.line.transform.SetPositionAndRotation( U( _c ), Sim.BoatToUnity( _q.setFromUnitVectors( _Y, _d.divideScalar( len ) ) ) );
				v.line.transform.localScale = new UnityEngine.Vector3( 1, ( float ) len, 1 );
			}
		}

		public void Dispose()
		{
			if ( root != null ) UnityEngine.Object.DestroyImmediate( root );
			root = null;
			foreach ( var m in new[] { anchorMesh, lineMesh, buoyMesh } ) if ( m != null ) UnityEngine.Object.DestroyImmediate( m );
			if ( material != null ) UnityEngine.Object.DestroyImmediate( material );
		}

		// an orange float with a pale mast, about 0.5 m tall (the trap buoys' shape, larger: the anchor buoy is a marker you look for)
		static BufferGeometry buildBuoy()
		{
			var P = new List<BufferGeometry>
			{
				prepare( sphere( 0.2, 10, 8 ), new Opts { color = 0xee5a1e, rough = 0.45, matrix = mat4( 0, 0, 0, 0, 0, 0, 1, 1.25, 1 ) } ),
				prepare( box( 0.03, 0.34, 0.03 ), new Opts { color = 0xf1ebd8, rough = 0.5, matrix = mat4( 0, 0.3, 0 ) } ),
				prepare( sphere( 0.045, 6, 5 ), new Opts { color = 0xee5a1e, rough = 0.5, matrix = mat4( 0, 0.5, 0 ) } ),
			};
			return GeoKit.mergePrepared( P );
		}

		// a stocked (Admiralty) anchor, about 0.9 m tall, standing on its crown: shank, a stock across the top and two arms with flukes
		static BufferGeometry buildAnchor()
		{
			var P = new List<BufferGeometry>();
			Opts iron( Matrix4 m = null ) => new Opts { color = 0x9aa1a6, rough = 0.45, metal = 0.4, matrix = m }; // galvanised: pale, so it shows on dark ground
			var V = new Func<double, double, double, Tidewater.Engine.Vector3>( ( x, y, z ) => new Tidewater.Engine.Vector3( x, y, z ) );
			P.Add( prepare( rod( V( 0, - 0.38, 0 ), V( 0, 0.44, 0 ), 0.024, 6 ), iron() ) );
			P.Add( prepare( rod( V( 0, 0.36, - 0.22 ), V( 0, 0.36, 0.22 ), 0.016, 5 ), iron() ) );
			P.Add( prepare( box( 0.08, 0.035, 0.018 ), iron( mat4( 0, 0.47, 0 ) ) ) ); // the ring the line is shackled to
			foreach ( int s in new[] { -1, 1 } )
			{
				P.Add( prepare( rod( V( 0, - 0.36, 0 ), V( s * 0.3, - 0.18, 0 ), 0.02, 6 ), iron() ) );
				P.Add( prepare( box( 0.13, 0.022, 0.1 ), iron( mat4( s * 0.32, - 0.17, 0, 0, 0, - s * 0.6 ) ) ) );
			}

			return GeoKit.mergePrepared( P );
		}
	}
}
