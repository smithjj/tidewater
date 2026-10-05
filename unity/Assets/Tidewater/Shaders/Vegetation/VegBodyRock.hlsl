// The rock surface (Rocks.js _createMaterial: surface + the LOD cross-fade) as the body of GetSurfaceAndBuiltinData, with `input`, `V` in scope: declares `s` ( .albedo .normal .roughness .ao ) and `Ns`.
	float3 Ns = vegM( normalize( input.tangentToWorld[ 2 ] ) );
	float3 Ps = vegM( GetAbsolutePositionWS( input.positionRWS ) );
	float cavity = input.texCoord0.x;

	TWRockGrad rg = TWImplicitGrad( Ps );
	float mcr = SAMPLE_TEXTURE2D( _TWDetailTex, sampler_TWDetailTex, TWRot2( Ps.xz, 0.9 ) / 61.0 ).w * 0.6
		+ SAMPLE_TEXTURE2D( _TWDetailTex, sampler_TWDetailTex, TWRot2( Ps.xz, 2.3 ) / 17.0 ).w * 0.4;
	TWRockSurfaceOut R = TWRockSurface( Ps, Ns, Ps.y, mcr, 0.5, 1.0, rg );
	// contact with the ground: sand / soil drifted against the base, darker crevice
	float ground = TWHeightAt( Ps.xz );
	float above = Ps.y - ground;
	float4 sp = vegRockSplat( Ps.xz );
	float contact = ( 1.0 - smoothstep( 0.0, 0.22, above + ( R.height - 0.5 ) * 0.15 ) ) * smoothstep( -0.2, 0.3, ground );
	float3 drift = lerp( S( 0.33, 0.27, 0.18 ), S( 0.8, 0.72, 0.56 ), sp.x );
	float3 nb = TWPerturbNormal( Ps, Ns, R.hd, 1.0 );
	// the LOD cross-fade: the incoming level keeps the dither cells below `fade`, the outgoing one the others
	float fadeT = vegBayer4( input.positionSS.xy );
	float rockFade = input.texCoord1.x;
	bool rockOut = input.texCoord1.y > 0.5;
	// (the shadow pass keeps every rock whole: a dithered shadow map crawls and pops, and the far level casts too)
#if ! defined( SHADERPASS ) || SHADERPASS != SHADERPASS_SHADOWS
	if ( ! ( rockOut ? ( fadeT >= rockFade ) : ( fadeT < rockFade ) ) ) discard;
#endif

	VegOut s;
	s.albedo = lerp( R.albedo, drift, contact * 0.8 );
	s.roughness = lerp( R.rough, 0.9, contact );
	s.normal = nb;
	s.ao = saturate( cavity * ( 1.0 - contact * 0.35 ) * ( smoothstep( 0.0, 0.35, R.height ) * 0.35 + 0.65 ) );
