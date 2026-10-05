using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.Util;
using Tidewater.World.Boat;
using UnityEngine;
using static Tidewater.World.Boat.GK;
using Quaternion = Tidewater.Engine.Quaternion;

// The Unity half of src/game/Traps.js: the pots on the seabed with their buoys and lines, the stack on the working boat's deck, and the one pot that is in the air on the
// hauler or over the stern. Traps (the logic) says where everything is in the sim frame; this puts GameObjects there. Material: the Boat shader's kind 8 (the game props: vertex
// colour, roughness and metal from the mesh, pattern 0), which is the JS `createPropMaterial( 'trap' )`.
//
// The meshes are the JS bake: unity/tools/dump-traps.mjs runs Traps.bake on the real GLBs and writes Resources/traps/{pot,buoy}.bytes (85,616 and 92,180 triangles, as the JS
// test counts them); the rope is the procedural unit cylinder scaled to the depth. Unity meshes of the boat frame are mirrored in x (UnityMesh), so a sim position (x, y, z) is
// Unity (x, y, -z) and a sim rotation goes through Sim.BoatToUnity; the deck stack is parented to the boat's own transform, where local x is the sim's -x.
//
// File: int32 'TRP1', int32 vertexCount, int32 indexCount, vertexCount x ( pos 3, normal 3, uv 2, colour 3, aux 4 ) float32, indexCount x uint32.
namespace Tidewater.Game
{
	public sealed class TrapsView
	{
		const int MAGIC = 0x31505254;

		// Resources/traps/<name>.bytes -> the JS-frame geometry (null when the file is missing or damaged)
		public static BufferGeometry LoadGeometry( string name )
		{
			var asset = Resources.Load<TextAsset>( "traps/" + name );
			if ( asset == null ) { Debug.LogError( "TrapsView: Resources/traps/" + name + ".bytes is missing (node unity/tools/dump-traps.mjs)" ); return null; }
			var b = asset.bytes;
			if ( b.Length < 12 || BitConverter.ToInt32( b, 0 ) != MAGIC ) { Debug.LogError( "TrapsView: " + name + ".bytes is not a trap file" ); return null; }
			int nv = BitConverter.ToInt32( b, 4 ), ni = BitConverter.ToInt32( b, 8 );
			const int F = 15;
			if ( b.Length != 12 + nv * F * 4 + ni * 4 ) { Debug.LogError( "TrapsView: " + name + ".bytes has the wrong size" ); return null; }
			var f = new float[ nv * F ]; var idx = new int[ ni ];
			Buffer.BlockCopy( b, 12, f, 0, nv * F * 4 );
			Buffer.BlockCopy( b, 12 + nv * F * 4, idx, 0, ni * 4 );
			var pos = new float[ nv * 3 ]; var nrm = new float[ nv * 3 ]; var uv = new float[ nv * 2 ]; var col = new float[ nv * 3 ]; var aux = new float[ nv * 4 ];
			for ( int i = 0; i < nv; i ++ )
			{
				int o = i * F;
				Array.Copy( f, o, pos, i * 3, 3 ); Array.Copy( f, o + 3, nrm, i * 3, 3 ); Array.Copy( f, o + 6, uv, i * 2, 2 );
				Array.Copy( f, o + 8, col, i * 3, 3 ); Array.Copy( f, o + 11, aux, i * 4, 4 );
			}

			var g = new BufferGeometry { name = name };
			g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
			g.setAttribute( "normal", new BufferAttribute( nrm, 3 ) );
			g.setAttribute( "uv", new BufferAttribute( uv, 2 ) );
			g.setAttribute( "color", new BufferAttribute( col, 3 ) );
			g.setAttribute( "aux", new BufferAttribute( aux, 4 ) );
			g.setIndex( new IndexBuffer( idx ) );
			return g;
		}

		sealed class SetObjs { public GameObject pot, buoy, rope; }

		readonly GameObject root, holder;
		readonly Transform deck;
		Transform coil; // the boat's green pot coil (BoatView's "boat-potCoil")
		readonly Material material;
		readonly UnityEngine.Mesh pot, buoy, rope;
		readonly Dictionary<int, SetObjs> sets = new Dictionary<int, SetObjs>();
		readonly List<int> gone = new List<int>();
		GameObject[] stack = new GameObject[ 0 ];
		static readonly Quaternion _q = new Quaternion();
		static readonly Euler _e = new Euler();

		TrapsView( Transform parent, Transform deck )
		{
			this.deck = deck;
			// a leftover from a domain reload (the objects are not saved): drop it
			var old = parent != null ? parent.Find( "Traps" ) : null;
			if ( old != null ) UnityEngine.Object.DestroyImmediate( old.gameObject );
			if ( deck != null ) for ( int i = deck.childCount - 1; i >= 0; i -- ) if ( deck.GetChild( i ).name.StartsWith( "TrapStack" ) ) UnityEngine.Object.DestroyImmediate( deck.GetChild( i ).gameObject );
			root = new GameObject( "Traps" ) { hideFlags = HideFlags.DontSave };
			if ( parent != null ) root.transform.SetParent( parent, false );

			material = new Material( Shader.Find( "Tidewater/Boat" ) ) { name = "trap", hideFlags = HideFlags.HideAndDontSave };
			material.SetFloat( "_BoatKind", 8 );
			material.SetFloat( "_CullMode", 2f );

			pot = LoadMesh( "pot" ); buoy = LoadMesh( "buoy" );
			rope = UnityMesh.Create( prepare( cylinder( 0.012, 0.012, 1, 5 ), new Opts { color = 0x9c8f74, rough = 0.95 } ), "TrapRope" );
			rope.hideFlags = HideFlags.DontSave;
			holder = Make( "Traps:haul", pot, root.transform, true );
			holder.SetActive( false );
		}

		// the Unity mesh of a baked file (kept to the view: the Editor destroys scene objects when Play starts, not these)
		static UnityEngine.Mesh LoadMesh( string name )
		{
			var g = LoadGeometry( name );
			if ( g == null ) return null;
			var m = UnityMesh.Create( g, "Trap-" + name );
			m.hideFlags = HideFlags.DontSave;
			return m;
		}

		public static TrapsView Create( Transform parent, Transform deck ) => new TrapsView( parent, deck );

		// false once the Editor has destroyed the objects (entering Play mode drops what the Editor made, while the C# objects survive)
		public bool Alive => root != null;

		GameObject Make( string name, UnityEngine.Mesh mesh, Transform parent, bool shadows )
		{
			var go = new GameObject( name ) { hideFlags = HideFlags.DontSave };
			go.transform.SetParent( parent, false );
			go.AddComponent<MeshFilter>().sharedMesh = mesh;
			var mr = go.AddComponent<MeshRenderer>();
			mr.sharedMaterial = material;
			mr.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
			mr.receiveShadows = true;
			return go;
		}

		static UnityEngine.Vector3 U( Tidewater.Engine.Vector3 p ) => Sim.ToUnity( p.x, p.y, p.z );

		// sim rotation about one axis -> the Unity rotation of a mirrored mesh
		static UnityEngine.Quaternion Rot( double x, double y, double z ) => Sim.BoatToUnity( _q.setFromEuler( _e.set( x, y, z ) ) );

		// the deck stack: one pot per place, in the boat's own frame (Unity local x is the sim's -x, so a turn about y reverses)
		void BuildStack( Traps t )
		{
			stack = new GameObject[ t.stackCount ];
			var p = new Tidewater.Engine.Vector3();
			for ( int i = 0; i < stack.Length; i ++ )
			{
				t.slotAt( i, p );
				var go = Make( "TrapStack" + i, pot, deck != null ? deck : root.transform, true );
				go.transform.localPosition = new UnityEngine.Vector3( ( float ) - p.x, ( float ) p.y, ( float ) p.z );
				go.transform.localRotation = UnityEngine.Quaternion.Euler( 0, ( float ) ( - t.slotYaw( i ) * 180 / Math.PI ), 0 );
				stack[ i ] = go;
			}
		}

		// after Traps.update: the pots, buoys and lines of the sets, the stack and the pot in the air
		public void Sync( Traps t )
		{
			if ( stack.Length != t.stackCount ) BuildStack( t );
			for ( int i = 0; i < stack.Length; i ++ ) if ( stack[ i ].activeSelf != t.stackShown[ i ] ) stack[ i ].SetActive( t.stackShown[ i ] );

			// the green coil rides on the last pot, and lies on the deck where that pot would stand while it is not aboard
			if ( coil == null && deck != null ) coil = deck.Find( "boat-potCoil" );
			if ( coil != null && DeckGear.POT_COIL_SLOT < t.stackShown.Length )
			{
				var lp = coil.localPosition; float y = t.stackShown[ DeckGear.POT_COIL_SLOT ] ? ( float ) DeckGear.POT_COIL_LIFT : 0f;
				if ( lp.y != y ) { lp.y = y; coil.localPosition = lp; }
			}

			gone.Clear();
			foreach ( var id in sets.Keys ) gone.Add( id );
			foreach ( var v in t.views )
			{
				gone.Remove( v.id );
				if ( ! sets.TryGetValue( v.id, out var o ) )
				{
					o = new SetObjs { pot = Make( "Trap" + v.id, pot, root.transform, true ), buoy = Make( "TrapBuoy" + v.id, buoy, root.transform, true ), rope = Make( "TrapLine" + v.id, rope, root.transform, false ) };
					o.pot.transform.SetPositionAndRotation( U( v.pot ), Rot( 0, v.potYaw, 0 ) );
					sets[ v.id ] = o;
				}

				o.buoy.transform.SetPositionAndRotation( U( v.buoy ), Rot( 0, 0, v.buoyRotZ ) );
				o.rope.transform.position = U( v.rope );
				o.rope.transform.localScale = new UnityEngine.Vector3( 1, ( float ) v.ropeScaleY, 1 );
			}

			foreach ( var id in gone ) { var o = sets[ id ]; UnityEngine.Object.DestroyImmediate( o.pot ); UnityEngine.Object.DestroyImmediate( o.buoy ); UnityEngine.Object.DestroyImmediate( o.rope ); sets.Remove( id ); }

			if ( holder.activeSelf != t.holderVisible ) holder.SetActive( t.holderVisible );
			if ( t.holderVisible ) holder.transform.SetPositionAndRotation( U( t.holderPos ), Sim.BoatToUnity( t.holderQuat ) );
		}

		public void Dispose()
		{
			if ( root != null ) UnityEngine.Object.DestroyImmediate( root );
			foreach ( var go in stack ) if ( go != null ) UnityEngine.Object.DestroyImmediate( go );
			foreach ( var m in new[] { pot, buoy, rope } ) if ( m != null ) UnityEngine.Object.DestroyImmediate( m );
			if ( material != null ) UnityEngine.Object.DestroyImmediate( material );
		}
	}
}
