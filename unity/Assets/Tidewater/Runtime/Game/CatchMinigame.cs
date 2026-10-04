using System;

// Port of src/game/CatchMinigame.js: the line-tension fight. Hold to reel: the line shortens and the tension climbs; let go and it eases off, but a
// running fish then takes line. Keep the tension in the green band: the fish tires there fastest. Too much tension (above the line's strength for about
// half a second) snaps it; too little for too long and the hook falls out. Every few seconds the fish surges: ease off or it breaks you.
//
//   var g = new CatchMinigame( species, kg, lineKg, reelSpeed, distance, rng );
//   g.update( dt, reeling ) -> "fighting" | "caught" | "snapped" | "escaped"
//   g.tension (0..1+, 1 = breaking point), g.distance (m), g.stamina (0..1), g.surge (0..1), g.band = { lo, hi } (the green band)
namespace Tidewater.Game
{
	public sealed class CatchMinigame
	{
		public readonly string species;
		public readonly double kg, reelSpeed, power, staminaMax, maxDistance;
		public double stamina = 1, distance, tension = 0.3, surge, slack, time;
		public readonly double[] band = { 0.3, 0.85 };
		public double overload; // seconds above the breaking point: the line snaps only if it's held there
		public string state = "fighting";
		public bool splashed; // Game's marker: the splash of the current run was heard
		readonly Func<double> rng;
		double _nextSurge, _surgeT;

		public CatchMinigame( string species, double kg, double lineKg = 7, double reelSpeed = 1.1, double distance = 15, Func<double> rng = null )
		{
			var f = FishTable.Get( species );
			this.rng = rng ?? ( () => UnityEngine.Random.value );
			this.species = species;
			this.kg = kg;
			this.reelSpeed = reelSpeed;
			// pull relative to the line: a fish near the line rating is hard, one far above it is a lottery
			power = Math.Min( 2.2, ( 0.25 + 0.75 * f.fight ) * Math.Pow( kg / Math.Max( lineKg * 0.5, 0.2 ), 0.55 ) );
			staminaMax = f.stamina * Math.Pow( Math.Max( kg / f.kgMax, 0.15 ), 0.35 );
			this.distance = distance;
			maxDistance = Math.Max( 60, distance + 40 ); // spooled: the fish takes all the line
			_nextSurge = 1.2 + this.rng() * 2;
		}

		public string update( double dt, bool reeling )
		{
			if ( state != "fighting" ) return state;
			time += dt;
			double tired = 1 - stamina; // 0 fresh .. 1 exhausted

			// surges: sudden runs, rarer and weaker as the fish tires
			_nextSurge -= dt;
			if ( _nextSurge <= 0 && _surgeT <= 0 )
			{
				_surgeT = 0.8 + rng() * 1.2 * ( 1 - tired * 0.6 );
				_nextSurge = ( 2 + rng() * 3 ) * ( 1 + tired );
			}

			_surgeT -= dt;
			double surgeTarget = _surgeT > 0 ? 1 : 0;
			surge += ( surgeTarget - surge ) * ( 1 - Math.Exp( - dt * 6 ) );

			// the fish's pull (0..~1.6) and the tension it and the reel make
			double pull = power * ( 0.35 + 0.65 * surge ) * ( 1 - 0.65 * tired );
			double target = reeling ? 0.25 + pull * 0.95 + 0.25 : pull * 0.72;
			double rate = reeling ? 2.2 : 3.0;
			tension += ( target - tension ) * ( 1 - Math.Exp( - dt * rate ) );

			// line in / out
			if ( reeling ) distance -= reelSpeed * dt * ( 1.15 - 0.6 * surge * ( 1 - tired ) );
			else distance += pull * 1.4 * dt;
			distance = Math.Max( 0, distance );

			// tiring: fastest in the band, slowly otherwise
			bool inBand = tension >= band[ 0 ] && tension <= band[ 1 ];
			stamina = Math.Max( 0, stamina - dt / staminaMax * ( inBand ? 1 : 0.3 ) );

			// outcomes
			overload = tension > 1 ? overload + dt : Math.Max( 0, overload - dt * 2 );
			if ( overload > 0.45 ) state = "snapped";
			else
			{
				slack = tension < 0.12 ? slack + dt : Math.Max( 0, slack - dt * 2 );
				if ( slack > 4 || distance > maxDistance ) state = "escaped";
				else if ( distance < 1.2 ) state = "caught";
			}

			return state;
		}
	}
}
