using System.Collections.Generic;
using System.Linq;
using Tidewater.World.Village;
using TerrainData = Tidewater.World.TerrainData;

namespace Tidewater.World.Vegetation
{
	// The site of the plants (Vegetation.js villageObstacles): the village's building footprints and boardwalk polylines are kept clear.
	public static class VegSites
	{
		public static VegSite FromVillage( TerrainData terrain, Tidewater.World.Village.Village village )
		{
			var fps = new List<VegFootprint>();
			var paths = new List<VegPath>();
			if ( village != null )
			{
				fps = village.getFootprints().Select( f => new VegFootprint { x = f.x, z = f.z, r = f.r } ).ToList();
				var all = new List<BoardwalkResult> { village.path };
				if ( village.sidePaths != null ) all.AddRange( village.sidePaths );
				foreach ( var p in all )
				{
					if ( p == null || p.samples == null ) continue;
					var vp = new VegPath { width = p.width > 0 ? p.width : 1.8 };
					for ( int i = 0; i < p.samples.Count; i += 5 ) vp.points.Add( new[] { p.samples[ i ].p.x, p.samples[ i ].p.z } );
					var last = p.samples[ p.samples.Count - 1 ];
					vp.points.Add( new[] { last.p.x, last.p.z } );
					paths.Add( vp );
				}
			}

			return new VegSite( terrain, 1234, fps, paths.Count > 0 ? paths : null );
		}
	}
}
