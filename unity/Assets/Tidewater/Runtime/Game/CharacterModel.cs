using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

// A vendor's animated character (the Rocketbox avatars of Resources/characters, converted by unity/tools/characters-to-fbx.py): the skinned model and its seven
// clips, played with the same crossfade layers as the JS SkinnedModel (engine/render/Skinning.js): play( name, fade, loop, speed, from ) starts a clip and fades
// the others out at the same rate, a clip that ends (not looping) calls onClipEnd, update( dt ) steps the layers and poses the model, hold() keeps the pose.
// Unity does the skinning (the JS has its own GPU skinning), through a manually stepped PlayableGraph.
namespace Tidewater.Game
{
	public sealed class CharacterModel
	{
		public readonly GameObject group; // the model: faces local +z in Unity's axes (the stall's figure faces sim +z, which is -z in Unity: the caller turns it)
		public Action<string> onClipEnd;

		sealed class Layer
		{
			public AnimationClip clip; public int slot; public double time, speed; public float weight, target, fade; public bool loop, ended;
		}

		readonly Dictionary<string, AnimationClip> clips = new Dictionary<string, AnimationClip>();
		readonly List<string> names = new List<string>();
		readonly List<Layer> layers = new List<Layer>();
		PlayableGraph graph;
		AnimationMixerPlayable mixer;
		readonly List<AnimationClipPlayable> slots = new List<AnimationClipPlayable>();
		readonly List<bool> used = new List<bool>();

		CharacterModel( GameObject group ) { this.group = group; }

		// "joe" / "marta" -> the model under `parent` (null when the assets are missing)
		public static CharacterModel Create( string name, Transform parent )
		{
			var prefab = Resources.Load<GameObject>( "characters/" + name );
			if ( prefab == null ) return null;
			var go = UnityEngine.Object.Instantiate( prefab, parent, false );
			go.name = "character:" + name;
			var anim = go.GetComponent<Animator>(); if ( anim == null ) anim = go.AddComponent<Animator>();
			foreach ( var a in Resources.LoadAll<Avatar>( "characters/" + name ) ) { anim.avatar = a; break; }
			anim.applyRootMotion = false;
			anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
			foreach ( var r in go.GetComponentsInChildren<SkinnedMeshRenderer>() ) { r.updateWhenOffscreen = true; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; r.receiveShadows = true; }
			var m = new CharacterModel( go );
			foreach ( var c in Resources.LoadAll<AnimationClip>( "characters/" + name ) )
				if ( ! c.name.StartsWith( "__preview__" ) ) { m.clips[ c.name ] = c; m.names.Add( c.name ); }
			if ( m.clips.Count == 0 ) { UnityEngine.Object.DestroyImmediate( go ); return null; }
			m.Graph( anim );
			return m;
		}

		void Graph( Animator anim )
		{
			graph = PlayableGraph.Create( group.name );
			graph.SetTimeUpdateMode( DirectorUpdateMode.Manual );
			// two slots per clip: a clip can be played again while its last run is still fading out
			mixer = AnimationMixerPlayable.Create( graph, names.Count * 2 );
			for ( int i = 0; i < names.Count * 2; i ++ )
			{
				var p = AnimationClipPlayable.Create( graph, clips[ names[ i / 2 ] ] );
				p.SetApplyFootIK( false ); p.SetSpeed( 0 ); p.SetTime( 0 );
				graph.Connect( p, 0, mixer, i );
				mixer.SetInputWeight( i, 0 );
				slots.Add( p ); used.Add( false );
			}

			var output = AnimationPlayableOutput.Create( graph, "pose", anim );
			output.SetSourcePlayable( mixer );
			graph.Play();
		}

		public bool Has( string name ) => clips.ContainsKey( name );
		public double clipDuration( string name ) => clips.TryGetValue( name, out var c ) ? c.length : 0;

		// the clip being played (the newest not fading out), or null
		public string current { get { foreach ( var l in layers ) if ( l.target == 1 ) return l.clip.name; return null; } }

		// Crossfade to a clip.
		public void play( string name, double fade = 0.4, bool loop = true, double speed = 1, double from = 0 )
		{
			if ( ! clips.TryGetValue( name, out var clip ) ) throw new Exception( "CharacterModel: no clip " + name );
			foreach ( var l in layers ) if ( l.clip == clip && l.target == 1 ) return;
			int slot = -1;
			int first = names.IndexOf( name ) * 2;
			for ( int k = 0; k < 2; k ++ ) if ( ! used[ first + k ] ) { slot = first + k; break; }
			if ( slot < 0 ) return;
			var layer = new Layer { clip = clip, slot = slot, time = from, weight = layers.Count > 0 ? 0 : 1, target = 1, fade = ( float ) Math.Max( fade, 1e-3 ), loop = loop, speed = speed };
			foreach ( var l in layers ) { l.target = 0; l.fade = layer.fade; }
			layers.Add( layer );
			used[ slot ] = true;
		}

		// the pose stays as it is while nobody sees it up close (nothing steps or poses the model until update is called again)
		public void hold() { }

		// dt = 0 poses without stepping the clips
		public void update( double dt )
		{
			var ended = new List<string>();
			foreach ( var l in layers )
			{
				l.time += dt * l.speed;
				double d = l.clip.length;
				if ( l.time >= d )
				{
					if ( l.loop && d > 0 ) l.time %= d;
					else
					{
						l.time = d;
						if ( ! l.ended ) { l.ended = true; if ( l.target == 1 ) ended.Add( l.clip.name ); }
					}
				}

				float step = ( float ) dt / l.fade;
				l.weight = l.target > l.weight ? Math.Min( l.target, l.weight + step ) : Math.Max( l.target, l.weight - step );
			}

			for ( int i = layers.Count - 1; i >= 0; i -- )
				if ( layers[ i ].target <= 0 && layers[ i ].weight <= 1e-4f ) { used[ layers[ i ].slot ] = false; mixer.SetInputWeight( layers[ i ].slot, 0 ); layers.RemoveAt( i ); }
			// fades in and out at the same rate keep the sum near 1: renormalise
			float W = 0; foreach ( var l in layers ) W += l.weight;
			foreach ( var l in layers )
			{
				var p = slots[ l.slot ];
				p.SetTime( l.time );
				mixer.SetInputWeight( l.slot, W > 1e-4f ? l.weight / W : l.weight );
			}

			graph.Evaluate( 0 );
			foreach ( var n in ended ) onClipEnd?.Invoke( n );
		}

		public void Dispose()
		{
			if ( graph.IsValid() ) graph.Destroy();
			if ( group != null ) UnityEngine.Object.DestroyImmediate( group );
		}
	}
}
