using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// The textures the canopy is drawn with, baked once on the GPU at start (the JS bakes them on the first update):
//   leaf    LeafTextures.js: the leaf-cluster cards ( 2 x 2 tiles of whorls of twig-end leaves ), Shaders/Vegetation/Bake/VegLeafBake.shader
//   impA/B  Impostors.js ImpostorAtlas.bake: every tree and shrub variant from 6 x 6 hemi-octahedral directions: A = ( leaf brightness, leaf flag, card random, coverage ),
//           B = ( local normal * 0.5 + 0.5, exposure ), Shaders/Vegetation/Bake/VegImpostorBake.shader
// canopyInfo.json ( written by dump-vegetation-geometry.mjs ): the lobe table of the variants and the frames ( size and centre ) of the tree and shrub impostors.
namespace Tidewater.World.Vegetation
{
	[Serializable] public sealed class VegGroupInfo { public float centerY, radius, rh, hv; }
	[Serializable] public sealed class VegCanopyInfo { public VegGroupInfo[] groups; public float[] lobeTable; public int treeVariants, shrubVariants; }

	public sealed class VegAtlases : IDisposable
	{
		public const int OCT_N = 6;     // frames per side
		public const int FRAME_PX = 128;
		const int LEAF_SIZE = 1024;

		public RenderTexture leaf, impA, impB;
		public VegCanopyInfo info;
		public int variantCount;
		public int shrubBase; // the first shrub variant in the atlas
		public Mesh quad;
		readonly List<Mesh> meshes = new List<Mesh>();

		public static VegCanopyInfo LoadInfo()
		{
			var ta = Resources.Load<TextAsset>( "vegetation/canopyInfo" );
			if ( ta == null ) throw new Exception( "vegetation/canopyInfo.json is not baked (node unity/tools/dump-vegetation-geometry.mjs)" );
			return JsonUtility.FromJson<VegCanopyInfo>( ta.text );
		}

		public VegAtlases( VegCanopyInfo info )
		{
			this.info = info;
			variantCount = info.treeVariants + info.shrubVariants;
			shrubBase = info.treeVariants;
			quad = BuildQuad();
		}

		// the quad of the impostor instances ( corners at +-1 )
		static Mesh BuildQuad()
		{
			var m = new Mesh { name = "veg-impostor-quad", hideFlags = HideFlags.HideAndDontSave };
			m.SetVertices( new[] { new Vector3( -1, -1, 0 ), new Vector3( 1, -1, 0 ), new Vector3( 1, 1, 0 ), new Vector3( -1, 1, 0 ) } );
			m.SetNormals( new[] { new Vector3( 0, 0, 1 ), new Vector3( 0, 0, 1 ), new Vector3( 0, 0, 1 ), new Vector3( 0, 0, 1 ) } );
			m.SetTriangles( new[] { 0, 2, 1, 0, 3, 2 }, 0 ); // (reversed: the vertex stage mirrors z)
			m.bounds = new Bounds( Vector3.zero, Vector3.one * 100f );
			return m;
		}

		static RenderTexture MakeRT( string name, int w, int h, int depth, FilterMode filter, TextureWrapMode wrap, int aniso )
		{
			var rt = new RenderTexture( w, h, depth, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear )
			{
				name = name, useMipMap = true, autoGenerateMips = false, filterMode = filter, wrapMode = wrap, anisoLevel = aniso, hideFlags = HideFlags.HideAndDontSave,
			};
			rt.Create();
			return rt;
		}

		public void Bake()
		{
			Release();
			BakeLeaf();
			BakeImpostors();
		}

		void BakeLeaf()
		{
			leaf = MakeRT( "vegLeafClusters", LEAF_SIZE, LEAF_SIZE, 0, FilterMode.Trilinear, TextureWrapMode.Repeat, 16 );
			var mat = new Material( Shader.Find( "Hidden/Tidewater/VegLeafBake" ) ) { hideFlags = HideFlags.HideAndDontSave };
			Graphics.Blit( Texture2D.blackTexture, leaf, mat );
			UnityEngine.Object.DestroyImmediate( mat );
			leaf.GenerateMips();
		}

		// hemi-octahedral decode: ( u, v ) in [ -1, 1 ]^2 -> unit direction with y >= 0
		public static Vector3 OctDecode( float u, float v )
		{
			float x = ( u - v ) * 0.5f, z = ( u + v ) * 0.5f;
			return new Vector3( x, 1 - Mathf.Abs( x ) - Mathf.Abs( z ), z ).normalized;
		}

		// frame transform: local plant -> atlas plane ( the cell's centre ) for the frame ( i, j ) of the cell column cellBase
		static Matrix4x4 FrameMatrix( VegGroupInfo g, int variantIndex, int i, int j )
		{
			float R = g.radius;
			var C = new Vector3( 0, g.centerY, 0 );
			var d = OctDecode( -1 + 2f * i / ( OCT_N - 1 ), -1 + 2f * j / ( OCT_N - 1 ) );
			var right = Vector3.Cross( Vector3.up, d );
			if ( right.sqrMagnitude < 1e-8f ) right = new Vector3( 1, 0, 0 );
			right.Normalize();
			var up = Vector3.Cross( d, right ).normalized;
			float cx = ( variantIndex * OCT_N + i + 0.5f ) * 2 * R, cy = ( j + 0.5f ) * 2 * R;
			var m = Matrix4x4.identity;
			m.SetRow( 0, new Vector4( right.x, right.y, right.z, -Vector3.Dot( C, right ) + cx ) );
			m.SetRow( 1, new Vector4( up.x, up.y, up.z, -Vector3.Dot( C, up ) + cy ) );
			m.SetRow( 2, new Vector4( d.x, d.y, d.z, -Vector3.Dot( C, d ) ) );
			m.SetRow( 3, new Vector4( 0, 0, 0, 1 ) );
			return m;
		}

		void BakeImpostors()
		{
			int W = variantCount * OCT_N * FRAME_PX, H = OCT_N * FRAME_PX;
			impA = MakeRT( "vegImpostorA", W, H, 24, FilterMode.Bilinear, TextureWrapMode.Clamp, 1 );
			impB = MakeRT( "vegImpostorB", W, H, 24, FilterMode.Bilinear, TextureWrapMode.Clamp, 1 );
			var shader = Shader.Find( "Hidden/Tidewater/VegImpostorBake" );
			// the variants of each group: the tree and shrub crowns with the lobe scales of their rows of the table applied
			var variants = new List<Mesh>[ 2 ] { new List<Mesh>(), new List<Mesh>() };
			for ( int v = 0; v < info.treeVariants; v ++ ) variants[ 0 ].Add( VegMesh.Load( "tree", info.lobeTable, v * 8 ) );
			for ( int v = 0; v < info.shrubVariants; v ++ ) variants[ 1 ].Add( VegMesh.Load( "shrub", info.lobeTable, ( info.treeVariants + v ) * 8 ) );
			meshes.AddRange( variants[ 0 ] ); meshes.AddRange( variants[ 1 ] );

			Shader.SetGlobalTexture( "_VegLeafAtlas", leaf );
			foreach ( var which in new[] { 0, 1 } )
			{
				var rt = which == 0 ? impA : impB;
				var mat = new Material( shader ) { hideFlags = HideFlags.HideAndDontSave };
				mat.SetFloat( "_VegBakeWhich", which );
				var cmd = new CommandBuffer { name = "veg impostor bake" };
				cmd.SetRenderTarget( rt );
				for ( int gi = 0; gi < 2; gi ++ )
				{
					var g = info.groups[ gi ];
					float R = g.radius;
					// the atlas plane is one orthographic view: every cell of this group is 2R wide ( the camera sits at z = 3R + 2 looking down -z )
					var proj = Matrix4x4.Ortho( 0, variantCount * OCT_N * 2 * R, 0, OCT_N * 2 * R, 0.1f, 6 * R + 10 );
					var view = Matrix4x4.Translate( new Vector3( 0, 0, -( 3 * R + 2 ) ) );
					cmd.SetGlobalMatrix( "_VegBakeVP", GL.GetGPUProjectionMatrix( proj, true ) * view );
					cmd.ClearRenderTarget( true, gi == 0, Color.clear );
					int vbase = gi == 0 ? 0 : shrubBase;
					for ( int vi = 0; vi < variants[ gi ].Count; vi ++ )
						for ( int j = 0; j < OCT_N; j ++ )
							for ( int i = 0; i < OCT_N; i ++ )
							{
								cmd.SetGlobalMatrix( "_VegBakeM", FrameMatrix( g, vbase + vi, i, j ) );
								cmd.DrawMesh( variants[ gi ][ vi ], Matrix4x4.identity, mat, 0, 0 );
							}
				}

				Graphics.ExecuteCommandBuffer( cmd );
				cmd.Release();
				UnityEngine.Object.DestroyImmediate( mat );
				rt.GenerateMips();
			}
		}

		// the shaders' globals of the canopy: the lobe table, the impostor groups' frames and the atlas layout
		public void SetGlobals()
		{
			var lt = new Vector4[ 10 ];
			for ( int i = 0; i < 10; i ++ ) lt[ i ] = new Vector4( info.lobeTable[ i * 4 ], info.lobeTable[ i * 4 + 1 ], info.lobeTable[ i * 4 + 2 ], info.lobeTable[ i * 4 + 3 ] );
			Shader.SetGlobalVectorArray( "_VegLobe", lt );
			var g0 = info.groups[ 0 ]; var g1 = info.groups[ 1 ];
			Shader.SetGlobalVector( "_VegGroup0", new Vector4( g0.centerY, g0.radius, g0.rh, g0.hv ) );
			Shader.SetGlobalVector( "_VegGroup1", new Vector4( g1.centerY, g1.radius, g1.rh, g1.hv ) );
			Shader.SetGlobalVector( "_VegImpInfo", new Vector4( shrubBase, variantCount * OCT_N, 0, 0 ) );
		}

		public void Release()
		{
			if ( leaf != null ) { leaf.Release(); UnityEngine.Object.DestroyImmediate( leaf ); leaf = null; }
			if ( impA != null ) { impA.Release(); UnityEngine.Object.DestroyImmediate( impA ); impA = null; }
			if ( impB != null ) { impB.Release(); UnityEngine.Object.DestroyImmediate( impB ); impB = null; }
			foreach ( var m in meshes ) if ( m != null ) UnityEngine.Object.DestroyImmediate( m );
			meshes.Clear();
		}

		public void Dispose()
		{
			Release();
			if ( quad != null ) UnityEngine.Object.DestroyImmediate( quad );
			quad = null;
		}
	}
}
