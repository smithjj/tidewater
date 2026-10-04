using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Port of the hull mask half of src/engine/render/SceneRenderer.js (addHullMask / _renderHullMasks): closed volumes the sea is not
// drawn inside. Each registered volume follows a transform; per camera the volumes that are in view and whose box does not contain
// the camera are drawn as the camera distance of the nearest face into a mask texture, which Water.hlsl reads to discard the sea
// behind that face (the surface would otherwise show through the cockpit sole when the stern squats or the boat heels).
// The mask is off while the camera is inside the box (under the hull / in the water around it, where masking would be wrong).
namespace Tidewater.Ocean
{
	public sealed class HullMask
	{
		sealed class Entry { public Mesh mesh; public Transform follow; public Bounds box; public Bounds bounds; }

		readonly List<Entry> entries = new List<Entry>();
		static readonly Plane[] planes = new Plane[ 6 ];
		static readonly int idMask = Shader.PropertyToID( "_TWHullMask" ), idParams = Shader.PropertyToID( "_TWHullParams" );
		Material material;
		MaterialPropertyBlock block = new MaterialPropertyBlock();
		RenderTexture rt;
		public int drawn; // volumes drawn for the last camera (debug)

		// `mesh` is in the local space of `follow` (Unity axes). `cullBox` (local space) defaults to the mesh bounds + 5 cm.
		public void Add( Mesh mesh, Transform follow, Bounds? cullBox = null )
		{
			var b = mesh.bounds;
			var box = cullBox ?? new Bounds( b.center, b.size + Vector3.one * 0.1f );
			entries.Add( new Entry { mesh = mesh, follow = follow, box = box, bounds = b } );
		}

		public void Remove( Transform follow ) => entries.RemoveAll( e => e.follow == follow );

		// returns whether any volume is masked for this camera, publishing the globals either way
		public bool Render( ScriptableRenderContext context, Camera cam )
		{
			drawn = 0;
			if ( entries.Count == 0 ) { Shader.SetGlobalVector( idParams, Vector4.zero ); return false; }
			if ( material == null )
			{
				var shader = Shader.Find( "Hidden/Tidewater/HullMask" );
				if ( shader == null ) return false;
				material = new Material( shader ) { hideFlags = HideFlags.HideAndDontSave };
				material.SetFloat( "_TWHullZTest", SystemInfo.usesReversedZBuffer ? ( float ) CompareFunction.GreaterEqual : ( float ) CompareFunction.LessEqual );
			}

			GeometryUtility.CalculateFrustumPlanes( cam.projectionMatrix * cam.worldToCameraMatrix, planes );
			var cp = cam.transform.position;
			var on = new List<Entry>();
			foreach ( var e in entries )
			{
				if ( e.follow == null ) continue;
				var local = e.follow.InverseTransformPoint( cp );
				if ( e.box.Contains( local ) ) continue;
				var wb = new Bounds( e.follow.TransformPoint( e.bounds.center ), Vector3.zero );
				float s = Mathf.Max( e.follow.lossyScale.x, Mathf.Max( e.follow.lossyScale.y, e.follow.lossyScale.z ) );
				wb.Expand( e.bounds.extents.magnitude * s * 2f ); // a cube around the bounding sphere
				if ( GeometryUtility.TestPlanesAABB( planes, wb ) ) on.Add( e );
			}
			if ( on.Count == 0 ) { Shader.SetGlobalVector( idParams, Vector4.zero ); return false; }

			int w = Mathf.Max( 1, cam.pixelWidth ), h = Mathf.Max( 1, cam.pixelHeight );
			if ( rt == null || rt.width != w || rt.height != h )
			{
				if ( rt != null ) rt.Release();
				rt = new RenderTexture( w, h, 24, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear ) { name = "Hull mask", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
				rt.Create();
			}

			var cmd = CommandBufferPool.Get( "Hull mask" );
			cmd.SetRenderTarget( rt );
			cmd.ClearRenderTarget( true, true, Color.clear, SystemInfo.usesReversedZBuffer ? 0f : 1f );
			var vp = GL.GetGPUProjectionMatrix( cam.projectionMatrix, true ) * cam.worldToCameraMatrix;
			foreach ( var e in on )
			{
				block.Clear();
				block.SetMatrix( "_TWHullM", e.follow.localToWorldMatrix );
				block.SetMatrix( "_TWHullVP", vp );
				block.SetVector( "_TWHullCam", cp );
				cmd.DrawMesh( e.mesh, Matrix4x4.identity, material, 0, 0, block );
				drawn ++;
			}
			context.ExecuteCommandBuffer( cmd );
			CommandBufferPool.Release( cmd );

			Shader.SetGlobalTexture( idMask, rt );
			Shader.SetGlobalVector( idParams, new Vector4( 1f, w, h, 0f ) );
			return true;
		}

		public void Dispose()
		{
			if ( rt != null ) { rt.Release(); Object.DestroyImmediate( rt ); rt = null; }
			if ( material != null ) { Object.DestroyImmediate( material ); material = null; }
			entries.Clear();
		}
	}
}
