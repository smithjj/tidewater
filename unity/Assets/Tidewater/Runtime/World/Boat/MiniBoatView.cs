using System.Collections.Generic;
using UnityEngine;

// The Unity view of the mini fishing boat (MiniFishingBoat.js, the mesh / material half): FactorBoatView over MiniBoatModel.
namespace Tidewater.World.Boat
{
	[ExecuteAlways]
	public sealed class MiniBoatView : FactorBoatView
	{
		protected override string Prefix => "mini-";
		protected override BoatModel CreateModel( out List<FactorMaterial> factors ) { var m = new MiniBoatModel(); factors = m.materials; return m; }
	}
}
