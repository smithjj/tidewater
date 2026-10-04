using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// The Unity view of the fish props (FishProps.build / cull, FishMaterial.js createPropMaterial): the fish, lobsters, ice and banana leaves of the
// market stall, drying racks and cleaning tables. The JS draws them as one instanced batch culled on the CPU once per frame; here every item is
// three renderers sharing the meshes of its kind (both levels of detail, and a low detail shadow proxy) and the same cull runs for every camera that
// renders (the renderers are enabled just before its own culling) (RenderPipelineManager.beginCameraRendering):
//   - nothing beyond DRAW_RANGE; the shadow proxy (level 1, shadows only) within SHADOW_RANGE (not frustum culled, like the JS);
//   - level of detail by distance (L * 28), cross-faded over a band (the screen-door of LODFade.js, in the shader), faded out near the draw range.
// The skin table (FishMaterial.js buildTable) is published as the global _FishSkin.
namespace Tidewater.World.Fish
{
	public sealed class FishPropsView
	{
		const double DRAW_RANGE = 90; // m
		const double SHADOW_RANGE = 22; // m: pieces closer than this cast shadows

		sealed class Item
		{
			public Transform t;
			public Renderer lod0, lod1, shadow;
			public Vector3 pos; public float L;
			public Vector4 a, b;
			public int state = - 1; public float fade;
			public MaterialPropertyBlock mpb0, mpb1;
		}

		readonly Transform root;
		readonly List<Item> items = new List<Item>();
		readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
		Material material;
		readonly Plane[] planes = new Plane[ 6 ];

		static readonly int idA = Shader.PropertyToID( "_FishA" ), idB = Shader.PropertyToID( "_FishB" ), idC = Shader.PropertyToID( "_FishC" );

		public int count => items.Count;

		public FishPropsView( Transform parent, FishProps props )
		{
			root = new GameObject( "village-fish" ) { hideFlags = HideFlags.DontSave }.transform;
			root.SetParent( parent, false );
			PublishSkin();
			material = new Material( Shader.Find( "Tidewater/Fish" ) ) { name = "village-fish", hideFlags = HideFlags.HideAndDontSave };
			foreach ( var it in props.items ) items.Add( Make( it ) );
			RenderPipelineManager.beginCameraRendering += OnBeginCamera;
		}

		public void Release()
		{
			RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
			foreach ( var m in meshes.Values ) if ( m != null ) UnityEngine.Object.DestroyImmediate( m );
			meshes.Clear();
			if ( material != null ) UnityEngine.Object.DestroyImmediate( material );
			if ( root != null ) UnityEngine.Object.DestroyImmediate( root.gameObject );
			items.Clear();
		}

		// the skin table: 8 rows per species (FishMaterial.js buildTable)
		static void PublishSkin()
		{
			var rows = new List<Vector4>();
			Vector4 V( Tidewater.Engine.Color k, double w ) => new Vector4( ( float ) k.r, ( float ) k.g, ( float ) k.b, ( float ) w );
			foreach ( var name in FishSpecies.NAMES )
			{
				var S = FishSpecies.SPECIES[ name ]; var K = FishSpecies.SKIN[ name ];
				double L = S.body;
				rows.Add( V( new Tidewater.Engine.Color( K.back ), S.metal * 0.55 ) );
				rows.Add( V( new Tidewater.Engine.Color( K.flank ), S.irid ) );
				rows.Add( V( new Tidewater.Engine.Color( K.belly ), K.rough ) );
				rows.Add( V( new Tidewater.Engine.Color( K.fin ), S.mouth.tip ) );
				rows.Add( V( new Tidewater.Engine.Color( K.edge ), S.scales ) );
				rows.Add( V( new Tidewater.Engine.Color( S.iris ), S.scaleVis ) );
				rows.Add( new Vector4( ( float ) ( 0.5 - S.eye.u * L ), ( float ) S.eye.y, ( float ) S.eye.r, ( float ) ( 0.5 - S.opercle * L ) ) );
				rows.Add( new Vector4( ( float ) S.lateral, ( float ) S.arch, ( float ) ( 0.5 - S.mouth.corner * L ), ( float ) S.mouth.y ) );
			}

			Shader.SetGlobalVectorArray( "_FishSkin", rows.ToArray() );
		}

		// unit model -> Unity mesh: z mirrored, winding reversed; uv0 / uv1 = aData, uv2 / uv3 = the rest position (the shader bends the vertices)
		Mesh MeshOf( string kind, string species, int lod )
		{
			string key = kind + ":" + ( species ?? "" ) + ":" + lod;
			if ( meshes.TryGetValue( key, out var m ) ) return m;
			var g = FishProps.geometryOf( kind, species, lod );
			var P = g.getAttribute( "position" ).array; var N = g.getAttribute( "normal" ).array; var D = g.getAttribute( "aData" ).array;
			int n = P.Length / 3;
			var v = new Vector3[ n ]; var nn = new Vector3[ n ]; var uv0 = new Vector2[ n ]; var uv1 = new Vector2[ n ]; var uv2 = new Vector2[ n ]; var uv3 = new Vector2[ n ];
			for ( int i = 0; i < n; i ++ )
			{
				v[ i ] = new Vector3( P[ i * 3 ], P[ i * 3 + 1 ], - P[ i * 3 + 2 ] );
				nn[ i ] = new Vector3( N[ i * 3 ], N[ i * 3 + 1 ], - N[ i * 3 + 2 ] );
				uv0[ i ] = new Vector2( D[ i * 4 ], D[ i * 4 + 1 ] );
				uv1[ i ] = new Vector2( D[ i * 4 + 2 ], D[ i * 4 + 3 ] );
				uv2[ i ] = new Vector2( P[ i * 3 ], P[ i * 3 + 1 ] );
				uv3[ i ] = new Vector2( P[ i * 3 + 2 ], 0f );
			}

			var src = g.index.array;
			var idx = new int[ src.Length ];
			for ( int i = 0; i + 2 < idx.Length; i += 3 ) { idx[ i ] = src[ i ]; idx[ i + 1 ] = src[ i + 2 ]; idx[ i + 2 ] = src[ i + 1 ]; }

			m = new Mesh { name = "fish-" + key, indexFormat = n > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16, hideFlags = HideFlags.HideAndDontSave };
			m.SetVertices( v ); m.SetNormals( nn ); m.SetUVs( 0, uv0 ); m.SetUVs( 1, uv1 ); m.SetUVs( 2, uv2 ); m.SetUVs( 3, uv3 );
			m.SetTriangles( idx, 0 );
			m.RecalculateBounds();
			// the bends (curl, sag, jaw) move the vertices in the shader
			var b = m.bounds; b.Expand( 0.6f ); m.bounds = b;
			meshes[ key ] = m;
			return m;
		}

		static Quaternion ToUnity( double[] q ) => new Quaternion( ( float ) - q[ 0 ], ( float ) - q[ 1 ], ( float ) q[ 2 ], ( float ) q[ 3 ] );

		Item Make( FishItem it )
		{
			var go = new GameObject( "fish-" + it.kind + ( it.species != null ? "-" + it.species : "" ) ) { hideFlags = HideFlags.DontSave };
			var t = go.transform;
			t.SetParent( root, false );
			var pos = Tidewater.Util.Sim.ToUnity( it.x, it.y, it.z );
			t.SetPositionAndRotation( pos, ToUnity( it.q ) );
			t.localScale = Vector3.one * ( float ) it.L;
			var m0 = MeshOf( it.kind, it.species, 0 ); var m1 = MeshOf( it.kind, it.species, 1 );
			Renderer R( string name, Mesh mesh, ShadowCastingMode shadows )
			{
				var c = new GameObject( name ) { hideFlags = HideFlags.DontSave };
				c.transform.SetParent( t, false );
				c.AddComponent<MeshFilter>().sharedMesh = mesh;
				var mr = c.AddComponent<MeshRenderer>();
				mr.sharedMaterial = material;
				mr.shadowCastingMode = shadows;
				mr.receiveShadows = true;
				mr.enabled = false;
				return mr;
			}

			var item = new Item
			{
				t = t, pos = pos, L = ( float ) it.L,
				lod0 = R( "lod0", m0, ShadowCastingMode.Off ), lod1 = R( "lod1", m1, ShadowCastingMode.Off ), shadow = R( "shadow", m1, ShadowCastingMode.ShadowsOnly ),
				a = new Vector4( ( float ) ( it.pattern + ( it.seed % 1 ) * 0.9 ), ( float ) it.curl, ( float ) it.sag, ( float ) it.jaw ),
				b = new Vector4( ( float ) it.flags[ 0 ], ( float ) it.flags[ 1 ], ( float ) it.flags[ 2 ], ( float ) it.flags[ 3 ] ),
				mpb0 = new MaterialPropertyBlock(), mpb1 = new MaterialPropertyBlock(),
			};
			Apply( item, 1f, false );
			return item;
		}

		// the per-draw values: the fish's record and the share / direction of the cross-fade of each level
		void Apply( Item it, float fade, bool outgoing )
		{
			it.mpb0.SetVector( idA, it.a ); it.mpb0.SetVector( idB, it.b ); it.mpb0.SetVector( idC, new Vector4( it.L, fade, outgoing ? 1f : 0f, 0f ) );
			it.mpb1.SetVector( idA, it.a ); it.mpb1.SetVector( idB, it.b ); it.mpb1.SetVector( idC, new Vector4( it.L, fade, 0f, 0f ) );
			it.lod0.SetPropertyBlock( it.mpb0 ); it.lod1.SetPropertyBlock( it.mpb1 );
			it.shadow.SetPropertyBlock( it.mpb1 );
		}

		// materials/LODFade.js bandFade
		static double BandFade( double dist, double start, double end )
		{
			double t = ( dist - start ) / Math.Max( end - start, 1e-6 );
			return t <= 0 ? 0 : t >= 1 ? 1 : t * t * ( 3 - 2 * t );
		}

		void OnBeginCamera( ScriptableRenderContext ctx, Camera cam )
		{
			if ( cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView ) return;
			Cull( cam );
		}

		// FishProps.cull
		void Cull( Camera cam )
		{
			GeometryUtility.CalculateFrustumPlanes( cam, planes );
			Vector3 c = cam.transform.position;
			double far = DRAW_RANGE * 0.9;
			foreach ( var it in items )
			{
				double d = ( it.pos - c ).magnitude;
				bool r0 = false, r1 = false, rs = false; float fade = 1f; bool outgoing = false;
				if ( d <= DRAW_RANGE )
				{
					rs = d < SHADOW_RANGE;
					bool inside = true;
					float rad = it.L * 0.8f;
					for ( int p = 0; p < 6 && inside; p ++ ) if ( planes[ p ].GetDistanceToPoint( it.pos ) < - rad ) inside = false;
					if ( inside )
					{
						// level of detail by distance, cross-faded over a band; faded out near the draw range
						double s = it.L * 28;
						if ( d > far ) { r0 = d < s; r1 = ! r0; fade = ( float ) ( 1 - BandFade( d, far, DRAW_RANGE ) ); }
						else if ( d > s * 0.88 && d < s ) { r0 = r1 = true; fade = ( float ) BandFade( d, s * 0.88, s ); outgoing = true; }
						else { r0 = d < s; r1 = ! r0; }
					}
				}

				// state: what the property blocks hold now
				int state = ( outgoing ? 2 : 0 ) | ( fade < 1f ? 1 : 0 );
				if ( state != it.state || Math.Abs( fade - it.fade ) > 1e-3f ) { Apply( it, fade, outgoing ); it.state = state; it.fade = fade; }
				if ( it.lod0.enabled != r0 ) it.lod0.enabled = r0;
				if ( it.lod1.enabled != r1 ) it.lod1.enabled = r1;
				if ( it.shadow.enabled != rs ) it.shadow.enabled = rs;
			}
		}
	}
}
