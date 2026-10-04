> **Note for this copy:** the reference photograph `Pacific-Spotted-Eagle-Ray-009.jpg` described below is not part of this repository (it is a third party's photograph), and the packed copy and the hidden reference object were removed from `spotted_eagle_ray.blend`. See `assets/README.md`.

# Spotted eagle ray

Open **spotted_eagle_ray.blend** in Blender. The finished model was built in Blender 5.2 through Blender MCP using the supplied photograph, `Pacific-Spotted-Eagle-Ray-009.jpg`.

- Continuous closed body and pectoral fins, a sculpted snout, pale underside, recessed spiracles, small eyes, mouth, nostrils, five pairs of gills, pelvic fins, dorsal fin, and a tapering whip tail with serrated barbs.
- Editable procedural ivory spots and skin shading, with rest-space UV coordinates. No external texture dependencies. The reference photograph is packed in the blend as a hidden reference.
- Select **EAGLE RAY | master transform** to move the animal. Collections **01** and **02** contain the model. Collection **03** contains the optional presentation scene. Keep the hidden spiracle cutters in collection **04** with the body.
- Press **Space** on the timeline to play the looping swim. It runs a full cycle in 72 frames at 30 fps (2.4 seconds): both pectoral fins sweep through upstroke and downstroke, the tail sways, and the body rises and rolls gently.
- A rendered loop is available as **spotted_eagle_ray_swim.gif**: 36 frames at 720 × 480, optimized for a 2.4-second seamless repeat.
- Select **EAGLE RAY | master transform** to adjust **Swim | strength**, **Swim | tail sway**, or **Swim | period (frames)** in the Object Properties. The four wing and tail shape keys also expose the poses directly.
- Four cameras and corresponding PNG previews: hero, dorsal, detail, and ventral. For the ventral camera, hide the seafloor and water and enable **Ventral inspection | softbox**.

The artistic reconstruction uses metres, with a 4.64 m disc span. The photograph is a visual reference, not a calibrated scan. Main body: 34,706 base vertices / 34,848 faces, with non-destructive subdivision and spiracle Booleans. Base mesh validation found no open or nonmanifold edges.

To rebuild in a connected Blender session, run `build_eagle_ray.py`, then `finish_eagle_ray.py`, `refine_and_render.py`, and `polish_anatomy.py`; run `animate_eagle_ray.py` last. The build script creates a new scene; an existing unrelated session is first saved to `working/previous_session.blend`. Run `render_eagle_ray.py` to regenerate the static presentation previews.

Anatomy was cross-checked against the [Florida Museum spotted eagle ray profile](https://www.floridamuseum.ufl.edu/discover-fish/species-profiles/spotted-eagle-ray/). Markings and presentation primarily follow the supplied Pacific ray photograph.
