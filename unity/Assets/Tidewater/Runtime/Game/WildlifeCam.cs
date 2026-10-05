using System;
using System.Collections.Generic;
using Tidewater.Player;
using Tidewater.World.Fish;
using UnityEngine;
using Engine3 = Tidewater.Engine.Vector3;

// The wildlife camera (the JS has the same one, src/player/WildlifeVisit.js; keep them together): G visits the animals in turn, the eagle ray, the stingrays and the turtle, and G again goes on to the next. The camera flies
// to a spot behind and beside the animal and stays with it as it swims (so it works under water and at the surface); the mouse orbits it, the wheel zooms, F leaves it to fly
// freely from there, Esc (or the free camera) ends the visit. The player stays where they were; the interface is hidden, as in photo mode, apart from a caption.
// G is the "wildlife" action in the bindings table, so it can be rebound and is on the controls sheet. While a visit is on the fish are calm (FishSchools.calm): the sim treats a camera
// under water as a diver, and the animal would swim away from it.
namespace Tidewater.Game
{
	public sealed class WildlifeCam
	{
		static readonly string[] MODELS = { "eagleRay", "stingray", "turtle" };
		static readonly Dictionary<string, string> NAMES = new Dictionary<string, string> { { "eagleRay", "Eagle ray" }, { "stingray", "Southern stingray" }, { "turtle", "Green turtle" } };

		public bool active { get; private set; }
		int index = -1, last = -1;
		readonly List<FishGroup> animals = new List<FishGroup>();
		FishSchools listed;
		FishGroup target;
		int fish;                         // the animal's index in the schools' arrays
		float fade;
		const double BEHIND = 0.5;        // where each visit starts from: a little round to the left of straight behind the animal
		double orbitYaw = BEHIND, orbitPitch = 0.22, zoom = 1, heading;
		readonly Engine3 tp = new Engine3(), _fwd = new Engine3(), _up = new Engine3( 0, 1, 0 );
		string caption = "";

		// the animals in a fixed order (the eagle ray, then the stingrays, then the turtle), found again if the schools were rebuilt
		void List( FishSchools s )
		{
			if ( s == listed && animals.Count > 0 ) return;
			listed = s; animals.Clear();
			if ( s == null ) return;
			foreach ( var m in MODELS ) foreach ( var g in s.groups ) if ( g.sp != null && g.sp.model == m ) animals.Add( g );
		}

		// once a frame, before the player is updated: G starts the visit or goes on to the next animal; Esc and the free camera end it
		public void Handle( PlayerHost host, GameHost game )
		{
			var inp = host.input;
			if ( active && ( host.freeCam || ( inp.actHit( "cancel" ) ) ) ) { Stop(); return; }
			if ( host.freeCam || ! inp.actHit( "wildlife" ) ) return;
			var view = FishSchoolsView.instance;
			List( view != null ? view.schools : null );
			if ( animals.Count == 0 ) { game.Toast( "No wildlife about", 2.2f ); return; }
			Visit( host, ( active ? index : last ) + 1 );
		}

		void Visit( PlayerHost host, int i )
		{
			index = last = ( i % animals.Count + animals.Count ) % animals.Count;
			target = animals[ index ];
			fish = target.offset;
			var s = listed;
			s.calm = true; // the camera must not frighten them (the sim treats a camera under water as a diver)
			tp.set( s.pos[ fish * 3 ], s.pos[ fish * 3 + 1 ], s.pos[ fish * 3 + 2 ] );
			heading = Math.Atan2( s.vel[ fish * 3 + 2 ], s.vel[ fish * 3 ] );
			if ( s.vel[ fish * 3 ] == 0 && s.vel[ fish * 3 + 2 ] == 0 ) heading = Math.Atan2( target.heading.z, target.heading.x );
			zoom = 1; orbitYaw = BEHIND; orbitPitch = 0.22;
			active = true;
			Place( host, 1, 0 );
		}

		public void Stop()
		{
			active = false; index = -1; target = null;
			if ( listed != null ) listed.calm = false;
		}

		// while a visit is on: the camera follows the animal (instead of the player or the free camera)
		public void Update( double dt, PlayerHost host )
		{
			if ( ! active || target == null || listed == null ) return;
			var inp = host.input;
			var look = inp.consumeLook();
			orbitYaw += look.x * 0.0035; // the view turns the way the mouse goes: the camera swings round the other way
			orbitPitch = Math.Max( - 0.5, Math.Min( 1.2, orbitPitch + look.y * 0.0035 ) );
			zoom = Math.Max( 0.5, Math.Min( 4, zoom * Math.Pow( 1.12, inp.consumeWheel() ) ) );
			Place( host, 1 - Math.Exp( - dt * 6 ), dt );
		}

		void Place( PlayerHost host, double follow, double dt )
		{
			var s = listed;
			double px = s.pos[ fish * 3 ], py = s.pos[ fish * 3 + 1 ], pz = s.pos[ fish * 3 + 2 ];
			double vx = s.vel[ fish * 3 ], vz = s.vel[ fish * 3 + 2 ], len = s.size[ fish ];
			tp.x += ( px - tp.x ) * follow; tp.y += ( py - tp.y ) * follow; tp.z += ( pz - tp.z ) * follow;
			if ( vx * vx + vz * vz > 0.0025 )
			{
				double want = Math.Atan2( vz, vx ), d = Math.Atan2( Math.Sin( want - heading ), Math.Cos( want - heading ) );
				heading += d * ( dt <= 0 ? 1 : 1 - Math.Exp( - dt * 1.5 ) );
			}

			// behind the animal, turned by the orbit, at a distance that shows all of it
			double dist = Math.Max( 2.4, len * 2.3 ) * zoom, a = heading + Math.PI + orbitYaw;
			double cp = Math.Cos( orbitPitch );
			double cx = tp.x + Math.Cos( a ) * cp * dist, cz = tp.z + Math.Sin( a ) * cp * dist, cy = tp.y + Math.Sin( orbitPitch ) * dist;
			cy = Math.Max( cy, host.terrainData.HeightAt( cx, cz ) + 0.3 );
			var cam = host.simCamera;
			cam.position.x += ( cx - cam.position.x ) * ( follow >= 1 ? 1 : 1 - Math.Exp( - dt * 5 ) );
			cam.position.y += ( cy - cam.position.y ) * ( follow >= 1 ? 1 : 1 - Math.Exp( - dt * 5 ) );
			cam.position.z += ( cz - cam.position.z ) * ( follow >= 1 ? 1 : 1 - Math.Exp( - dt * 5 ) );
			_fwd.set( tp.x - cam.position.x, tp.y - cam.position.y, tp.z - cam.position.z );
			if ( _fwd.lengthSq() < 1e-6 ) return;
			cam.lookAlong( _fwd.normalize(), _up );

			double depth = Math.Max( 0, Tidewater.Core.G.cameraWaterHeight - py );
			string name = NAMES.TryGetValue( target.sp.model, out var n ) ? n : target.sp.model;
			caption = $"{name}  ·  {len.ToString( "F1", System.Globalization.CultureInfo.InvariantCulture )} m  ·  {( depth < 0.3 ? "at the surface" : depth.ToString( "F1", System.Globalization.CultureInfo.InvariantCulture ) + " m deep" )}";
		}

		public void OnGUI()
		{
			if ( Event.current.type != EventType.Repaint ) return;
			fade = UIKit.Approach( fade, active ? 1 : 0, 0.3f, Time.unscaledDeltaTime );
			if ( fade <= 0.001f ) return;
			float u = UIKit.U, a = UIScale.Ease( fade );
			var title = UIKit.Style( UIFonts.InterSemi, 14, TextAnchor.MiddleCenter ); title.normal.textColor = Tint( UIKit.INK, a );
			var hint = UIKit.Style( UIFonts.Inter, 11.5f, TextAnchor.MiddleCenter ); hint.normal.textColor = Tint( UIKit.INK3, a );
			var inp = GameHost.instance.Host.input;
			string h = $"{inp.label( "wildlife" )} next animal  ·  mouse look, wheel zoom  ·  {inp.label( "freeCam" )} fly freely  ·  {inp.label( "cancel" )} leave";
			float w = Mathf.Max( title.CalcSize( new GUIContent( caption ) ).x, hint.CalcSize( new GUIContent( h ) ).x ) + 40 * u, ht = 56 * u;
			var r = new Rect( ( Screen.width - w ) / 2, Screen.height - UIScale.Edge - ht + ( 1 - a ) * 10 * u, w, ht );
			UIKit.Glass( r, 16 * u, a );
			GUI.Label( new Rect( r.x, r.y + 8 * u, w, 22 * u ), caption, title );
			GUI.Label( new Rect( r.x, r.y + 30 * u, w, 18 * u ), h, hint );
		}

		static Color Tint( Color c, float a ) { c.a *= a; return c; }
	}
}
