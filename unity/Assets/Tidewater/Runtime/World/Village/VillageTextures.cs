using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

// Port of src/world/village/TextureBaker.js VillageTextures: bakes the nine tileable PBR sets of the village materials on the GPU
// (Shaders/Village/VillageBake.compute: a "fields" kernel per set plus the Sobel / horizon-AO derivation kernel) and binds the
// finished maps as the globals vlgWoodA, vlgWoodN, vlgPaintN, vlgRoofA ... vlgNet (see VillageSurface.hlsl).
//   *A maps: albedo / mask, RGBA8;   *N maps: RG = normal xy, B = roughness, A = AO, RGBA8.
// All final maps are RGBA8 (linear), fully mipmapped, repeat, trilinear + 8x anisotropic: ~43 MB. The transient field maps
// (RGBA16F) are released right after the derivation pass.
namespace Tidewater.World.Village
{
	public static class VillageTextures
	{
		struct Set
		{
			public string name; public int w, h; public double mx, my, hs, ao; public string output, nra;
			public Set( string name, int w, int h, double mx, double my, double hs, double ao, string output, string nra )
			{ this.name = name; this.w = w; this.h = h; this.mx = mx; this.my = my; this.hs = hs; this.ao = ao; this.output = output; this.nra = nra; }
		}

		// out: name of the albedo / mask map, nra: name of the normal-roughness-AO map
		static readonly Set[] SETS =
		{
			new Set( "wood", 1024, 1024, 2.0, 1.0, 0.0022, 1.2, "woodA", "woodN" ),
			new Set( "paint", 512, 512, 1.0, 0.5, 0.0016, 0.6, null, "paintN" ),
			new Set( "roof", 512, 1024, 0.84, 1.68, 0.022, 0.9, "roofA", "roofN" ),
			new Set( "thatch", 1024, 1024, 1.0, 1.0, 0.035, 1.3, "thatchA", "thatchN" ),
			new Set( "stone", 1024, 1024, 2.0, 2.0, 0.02, 1.1, "stoneA", "stoneN" ),
			new Set( "hard", 512, 512, 1.0, 1.0, 0.0012, 0.8, "hardA", "hardN" ),
			new Set( "grime", 512, 512, 0, 0, 0, 0, "grime", null ),
			new Set( "rope", 256, 256, 0, 0, 0, 0, "rope", null ),
			new Set( "net", 256, 256, 0, 0, 0, 0, "net", null ),
		};

		static readonly Dictionary<string, RenderTexture> textures = new Dictionary<string, RenderTexture>();
		public static double bakeMs;
		public static double bytes;

		// shader global of a map: woodA -> vlgWoodA
		static string GlobalName( string map ) => "vlg" + char.ToUpperInvariant( map[ 0 ] ) + map.Substring( 1 );

		public static bool Baked()
		{
			// the maps outlive a domain reload (native objects bound as globals): adopt them rather than bake again
			var a = Shader.GetGlobalTexture( "vlgNet" ) as RenderTexture;
			var b = Shader.GetGlobalTexture( "vlgWoodN" ) as RenderTexture;
			return a != null && a.IsCreated() && b != null && b.IsCreated();
		}

		static RenderTexture Final( string name, int w, int h )
		{
			// RGBA8, full mip chain; sampled trilinear + 8x anisotropic, repeat
			var rt = new RenderTexture( w, h, 0, GraphicsFormat.R8G8B8A8_UNorm )
			{
				name = "vlg_" + name, enableRandomWrite = true, useMipMap = true, autoGenerateMips = false,
				wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8, hideFlags = HideFlags.HideAndDontSave,
			};
			rt.Create();
			textures[ name ] = rt;
			bytes += w * h * 4.0 * 4.0 / 3.0;
			return rt;
		}

		// Bakes (once; force = true bakes again, e.g. after editing the compute shader).
		public static void Ensure( ComputeShader cs, bool force = false )
		{
			if ( ! force && Baked() ) return;
			if ( cs == null ) { Debug.LogError( "VillageTextures: the bake compute shader is missing" ); return; }
			var sw = System.Diagnostics.Stopwatch.StartNew();
			Release();
			bytes = 0;
			int kDerive = cs.FindKernel( "Derive" );
			foreach ( var set in SETS )
			{
				RenderTexture fields = null, albedo = null, nra = null;
				if ( set.nra != null )
					fields = new RenderTexture( set.w, set.h, 0, GraphicsFormat.R16G16B16A16_SFloat ) { name = "vlgFields_" + set.name, enableRandomWrite = true, hideFlags = HideFlags.HideAndDontSave };
				if ( fields != null ) fields.Create();
				if ( set.output != null ) albedo = Final( set.output, set.w, set.h );
				if ( set.nra != null ) nra = Final( set.nra, set.w, set.h );

				int k = cs.FindKernel( "Fields_" + set.name );
				cs.SetInts( "_Dim", set.w, set.h );
				if ( fields != null ) cs.SetTexture( k, "_OutFields", fields );
				if ( albedo != null ) cs.SetTexture( k, "_OutAlbedo", albedo );
				cs.Dispatch( k, ( set.w + 7 ) / 8, ( set.h + 7 ) / 8, 1 );
				if ( albedo != null ) albedo.GenerateMips();

				if ( nra != null )
				{
					// SLOPE, AO (TextureBaker.js deriveCode)
					double sx = set.hs / ( set.mx / set.w ), sy = set.hs / ( set.my / set.h );
					double ao1 = set.hs / ( ( set.mx / set.w + set.my / set.h ) * 0.5 );
					cs.SetVector( "_Derive", new Vector4( ( float ) sx, ( float ) sy, ( float ) set.ao, ( float ) ao1 ) );
					cs.SetInts( "_Dim", set.w, set.h );
					cs.SetTexture( kDerive, "_Fields", fields );
					cs.SetTexture( kDerive, "_OutNRA", nra );
					cs.Dispatch( kDerive, ( set.w + 7 ) / 8, ( set.h + 7 ) / 8, 1 );
					nra.GenerateMips();
				}

				// the transient field map: freed once the passes above are queued (the release is ordered after them)
				if ( fields != null ) { fields.Release(); Object.DestroyImmediate( fields ); }
			}

			foreach ( var kv in textures ) Shader.SetGlobalTexture( GlobalName( kv.Key ), kv.Value );
			bakeMs = sw.Elapsed.TotalMilliseconds;
		}

		static void Release()
		{
			foreach ( var kv in textures ) if ( kv.Value != null ) { kv.Value.Release(); Object.DestroyImmediate( kv.Value ); }
			textures.Clear();
		}
	}
}
