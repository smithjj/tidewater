// Runs laceData() of src/ocean/SurfFoam.js (the CPU lace texture) without a GPU: the module is loaded from its own source with the
// engine import replaced by stubs, so the code under test is the original.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

export async function makeLaceTexture( root ) {

	const file = path.join( root, 'src/ocean/SurfFoam.js' );
	let src = fs.readFileSync( file, 'utf8' );
	src = src.replace( /import \{ Texture, ShaderModule, commonModule \} from [^\n]+\n/, 'class Texture { constructor( o ) { Object.assign( this, o ); this.uploads = []; } upload() {} }\nclass ShaderModule { constructor( o ) { Object.assign( this, o ); } }\nconst commonModule = {};\n' );
	const tmp = path.join( os.tmpdir(), 'tw-surf-foam.mjs' );
	fs.writeFileSync( tmp, src + '\nexport const __laceData = laceData;\n' );
	const mod = await import( tmp );
	return mod.__laceData( 512 );

}
