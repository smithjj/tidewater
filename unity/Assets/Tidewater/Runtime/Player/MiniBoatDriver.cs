using Tidewater.Core;
using Tidewater.Ocean;
using Tidewater.World;
using Tidewater.World.Boat;
using UnityEngine;

// Hosts a BoatController on the mini fishing boat's MiniBoatView (App.js: miniCtl): builds the controller once the sea's water queries exist, and
// each frame queues the queries and steps it (PlayerHost calls Tick in the App.js order; the Player sets its controls while it is aboard).
// Unlike the lobster boat's BoatDriver it has no spray, no wake and no lights (the JS gives the mini none either).
namespace Tidewater.Player
{
	[ExecuteAlways]
	public sealed class MiniBoatDriver : MonoBehaviour
	{
		public BoatController controller { get; private set; }
		MiniBoatView view;
		[System.NonSerialized] bool masked;

		public bool Ensure()
		{
			if ( controller != null ) return true;
			var ocean = OceanRenderer.instance;
			var terrain = FindAnyObjectByType<TerrainRenderer>();
			view = GetComponent<MiniBoatView>();
			if ( ocean == null || ocean.query == null || terrain == null || terrain.data == null || view == null || view.model == null ) return false;
			controller = new BoatController( view.model.dynamics(), ocean.query, terrain.data.HeightAt, terrain.colliders, BoatDock.Mini ) { view = view, boatModel = view.model };
			// the sea is not drawn inside the hull; the mask is off inside the below-waterline box only (the angler sits above it)
			ocean.hullMask.Add( Tidewater.Engine.UnityMesh.Create( view.model.createHullVolumeGeometry(), "mini-hullmask" ), view.transform, MiniBoatModel.MASK_CULL_BOX );
			masked = true;
			return true;
		}

		public void Tick( double dt )
		{
			if ( ! Ensure() ) return;
			controller.queueQueries();
			controller.update( dt );
			view.Tick( dt );
		}

		void OnDestroy()
		{
			var ocean = OceanRenderer.instance;
			if ( masked && ocean != null && view != null ) ocean.hullMask.Remove( view.transform );
		}
	}
}
