// The grass surface (GrassField.js createGrassMaterial: surface) as the body of GetSurfaceAndBuiltinData, with `input`, `V` in scope: declares `s` ( .albedo .normal .roughness .ao ) and `Ns`.
	float3 geoN = vegM( normalize( input.tangentToWorld[ 2 ] ) );
	float3 Ns = geoN;
	float3 Vs = vegM( V );

	float hf = input.color.x;
	float kind = floor( input.color.y * 0.5 + 0.01 );
	float centre = saturate( input.color.y - 2.0 * kind ); // 1 at a leaf / flower centre
	float duneF = input.color.z;
	float rnd = input.color.w;
	float across = input.texCoord1.x;
	float gustB = input.texCoord1.y;
	float dryBlade = input.texCoord2.x;
	VegMeadow mt = vegMeadowTone( input.texCoord0.x, input.texCoord0.y, 0.0, 0.0 );

	// the self-shadowing of the sward: dark at the base of the clump
	float ao = lerp( 0.32, 1.0, vsm( 0.0, 0.75, hf ) );
	// dune grass: olive base, straw tips; meadow: the terrain's meadow tone, dark and brownish (dead leaf sheaths) at the base, lighter / sun-bleached towards the tips
	float3 cSoil = vegSrgb( float3( 0.17, 0.13, 0.08 ) ), cStraw = vegSrgb( float3( 0.62, 0.54, 0.33 ) );
	float3 duneBase = lerp( vegC( 0x5d6232 ), vegC( 0x7f8a40 ), rnd );
	float3 duneTip = lerp( vegC( 0xb8ab6c ), vegC( 0x9aa452 ), rnd );
	float3 tone = mt.tone * ( rnd * 0.34 + 0.83 );
	float3 lushBase = lerp( tone * 0.5, cSoil, 0.3 );
	float tipDry = mt.dry * 0.4 + vsm( 0.8, 1.0, rnd ) * 0.35;
	float3 lushTip = lerp( tone * float3( 1.25, 1.25, 1.05 ), cStraw, tipDry * vsm( 0.55, 1.0, hf ) );
	float3 gBase = lerp( lushBase, duneBase, duneF );
	float3 gTip = lerp( lushTip, duneTip, duneF );
	float3 grass0 = lerp( gBase, gTip, vsm( 0.05, 0.95, hf ) );
	// dead blades: straw to brown
	grass0 = lerp( grass0, lerp( cStraw, cSoil * 1.8, rnd * 0.6 ) * ( vsm( 0.0, 0.5, hf ) * 0.4 + 0.6 ), dryBlade * ( 1.0 - duneF ) );
	// the pale midrib
	grass0 = grass0 * ( pow( 1.0 - abs( across ), 6.0 ) * vsm( 0.05, 0.4, hf ) * 0.18 + 1.0 );
	// wind sheen: blades flattened by a gust show their paler undersides, so gusts read as bright waves rolling across the grass
	float3 grass = lerp( grass0, grass0 * float3( 1.3, 1.28, 1.1 ) + float3( 0.03, 0.03, 0.015 ), gustB * vsm( 0.15, 0.9, hf ) * 0.6 );
	float3 oatStalk = lerp( vegC( 0x9c9a62 ), vegC( 0xc2b27a ), hf );
	float3 oatHead = lerp( vegC( 0xc9b27a ), vegC( 0xa8905a ), rnd );
	float3 vine = lerp( lerp( vegC( 0x2e5219 ), vegC( 0x4a7328 ), rnd ), vegC( 0x7a8f4a ), centre * 0.3 ) * ( hf < 0.075 ? float3( 1.25, 1.05, 0.8 ) : float3( 1.0, 1.0, 1.0 ) );
	float3 flower = lerp( vegC( 0xb8479c ), vegC( 0xf2e8ee ), vsm( 0.35, 0.9, centre ) );
	float3 c = kind < 0.5 ? grass : ( kind < 1.5 ? oatStalk : ( kind < 2.5 ? oatHead : ( kind < 3.5 ? vine : flower ) ) );

	VegOut s;
	s.albedo = c * ( kind < 2.5 ? ao : 1.0 );
	s.roughness = ( kind > 2.5 && kind < 3.5 ) ? 0.45 : 0.8;
	s.normal = normalize( normalize( Ns ) + Vs * 0.4 );
	s.ao = 1.0;
