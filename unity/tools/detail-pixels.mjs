// Runs the pixel loop of src/world/terrain/DetailTextures.js without a GPU: the module is loaded from its
// own source with the Texture / generateMipmaps imports replaced by stubs, so the code under test is the
// original, unmodified.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

export async function getDetailPixels( root ) {

	const file = path.join( root, 'src/world/terrain/DetailTextures.js' );
	let src = fs.readFileSync( file, 'utf8' );
	src = src.replace( /import \{ Texture \} from [^\n]+\n/, 'class Texture { constructor( o ) { Object.assign( this, o ); } getGPU() {} }\n' );
	src = src.replace( /import \{ generateMipmaps \} from [^\n]+\n/, 'const generateMipmaps = () => {};\n' );
	src = src.replace( "from './TerrainNoise.js'", `from '${ path.join( root, 'src/world/terrain/TerrainNoise.js' ) }'` );
	const tmp = path.join( os.tmpdir(), 'tw-detail-textures.mjs' );
	fs.writeFileSync( tmp, src );
	const mod = await import( tmp );
	return mod.getDetailTexture().image.data;

}
