using System.IO;
using Tidewater.Audio;
using UnityEditor;
using UnityEngine;

// Import settings for Resources/audio (SoundScape's clips): the loops stay compressed in memory (a minute of stereo is 11 MB decoded), the
// one-shot sprites are PCM and decompressed on load, because UnityBackend cuts them into one AudioClip per slice with GetData.
namespace Tidewater.EditorTools
{
	public sealed class AudioImport : AssetPostprocessor
	{
		void OnPreprocessAudio()
		{
			if ( ! assetPath.StartsWith( "Assets/Tidewater/Resources/audio/" ) ) return;
			var imp = ( AudioImporter ) assetImporter;
			if ( ! SoundBank.BANK.TryGetValue( Path.GetFileNameWithoutExtension( assetPath ), out var clip ) )
			{
				// the bank is keyed by name, files by file name (the same except for none, today)
				foreach ( var c in SoundBank.BANK.Values ) if ( c.file == Path.GetFileNameWithoutExtension( assetPath ) ) { clip = c; break; }
			}

			if ( clip == null ) return;
			var s = imp.defaultSampleSettings;
			if ( clip.loop ) { s.loadType = AudioClipLoadType.CompressedInMemory; s.compressionFormat = AudioCompressionFormat.Vorbis; s.quality = 0.9f; }
			else { s.loadType = AudioClipLoadType.DecompressOnLoad; s.compressionFormat = AudioCompressionFormat.PCM; }
			s.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
			imp.defaultSampleSettings = s;
			imp.forceToMono = false;
			imp.loadInBackground = false;
		}
	}
}
