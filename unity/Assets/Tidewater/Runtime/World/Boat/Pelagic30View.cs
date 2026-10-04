using System.Collections.Generic;
using UnityEngine;

// The Unity view of the Pelagic 30 (Pelagic30.js, the mesh / material half): FactorBoatView over Pelagic30.
namespace Tidewater.World.Boat
{
	[ExecuteAlways]
	public sealed class Pelagic30View : FactorBoatView
	{
		protected override string Prefix => "pelagic-";
		protected override BoatModel CreateModel( out List<FactorMaterial> factors ) { var m = new Pelagic30(); factors = m.materials; return m; }
	}
}
