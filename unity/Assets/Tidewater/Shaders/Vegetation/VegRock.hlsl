// The scattered rocks (src/world/Rocks.js): one procedural instanced draw per rock shape and level of detail. The surface is the terrain's rock ( TerrainShading.hlsl TWRockSurface ).

#ifndef VEG_ROCK_INCLUDED
#define VEG_ROCK_INCLUDED

StructuredBuffer<float4> _RockInst; // per instance: the rows of the 3 x 4 matrix ( the sim frame ), then ( fade, outgoing, 0, 0 )
TEXTURE2D( _TWSplatTex ); SAMPLER( sampler_TWSplatTex ); // sand, paths, gullies / seagrass, rubble / scarp

float4 vegRockSplat( float2 xz )
{
	float2 uv = ( xz - _TWTerrainParams.x ) / _TWTerrainParams.y;
	return SAMPLE_TEXTURE2D( _TWSplatTex, sampler_TWSplatTex, uv );
}

#endif // VEG_ROCK_INCLUDED
