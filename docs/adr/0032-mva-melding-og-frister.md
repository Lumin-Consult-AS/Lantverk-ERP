# ADR-0032: MVA-melding og frister

Status: akseptert (1. november 2026) for generering, validering og frister. **Innsending er ikke bygget**: den venter på B8 (systemleverandørstatus, Maskinporten, systembruker, avklaring med Knowit). Planens «Ferdig når» (validert og sendt mot Skatteetatens testmiljø) er derfor **ikke oppfylt**.

## Beslutning

- **Formatet er Skatteetatens eget.** MVA-meldingen lages som `mvaMeldingDto` etter skjema v1.0 (`no:skatteetaten:fastsetting:avgift:mva:skattemeldingformerverdiavgift:v1.0`, hentet fra Skatteetatens `mva-meldingen` på GitHub og lagt inn i Norge-pakken). Testene sammenligner det vi lager med to av Skatteetatens egne eksempelfiler (kode 3 og kode 1), element for element, og sjekker at tre av deres eksempler passerer vår kopi av skjemaet.
- **Meldingen lages fra MVA-oppgjøret** (`norway.vatreturn`, beregnet) i en ny post `norway.vatsubmission` («MVA-melding»). Å opprette posten ber om en melding (hendelsen `norway.vatsubmission.requested@1`); handleren lager XML-en, kontrollerer den, lagrer filen og setter status `valid`, `invalid` eller `failed` med begrunnelse. En melding som ikke består lagres aldri som fil.
- **Innholdet:** én linje per SAF-T MVA-kode. Utgående: grunnlag, sats (som i kodelisten, «11,11» med komma) og positiv avgift. Inngående: bare avgiften, **negativ**, uten grunnlag og sats. `fastsattMerverdiavgift` er summen av linjene. Alle beløp er hele kroner (regel R082), avrundet per linje, slik at summen alltid stemmer (R018). Kodene 0, 7, 20, 21 og 22 rapporteres ikke og står i meldingen som notat. Kategori er `alminnelig` eller `primaernaering`.
- **Perioden** leses fra terminen: en måned, to måneder fra januar, tre fra januar, seks måneder eller året. Andre terminer gir `failed` med forklaring, i stedet for en melding Skatteetaten ville avvise.
- **Kontroller** (utover skjemaet): organisasjonsnummer på ni sifre (R000), linjer finnes når beløpet ikke er 0 (R084), utgående linjer har grunnlag og sats og inngående har ingen (R066), KID er gyldig MOD10/MOD11 (R079), og avgift ≈ grunnlag × sats innenfor 1 kr (R019, bare advarsel). Skatteetatens regler som trenger deres registre (plikter, tidligere meldinger, kontonummer for utbetaling) kan bare kjøres av deres testmiljø.
- **Frister** (`deadline` på MVA-oppgjøret): den 10. i den andre måneden etter at terminen slutter (termin januar–februar: 10. april), flyttet til neste virkedag ved helg og norske helligdager (inkludert påskedagene, Kristi himmelfart og pinse, regnet ut). På startsiden vises «MVA-melding som er forfalt» (handling) og «MVA-melding som skal leveres» (advarsel) for oppgjør som ikke er markert `submitted`. Inntil innsending finnes, krysser brukeren av for hånd etter levering i Altinn.
- **KID for MVA-betalingen** (Skatteetatens betalingsinformasjon) legges i Norsk oppsett (`vatPaymentKid`) og tas med i meldingen når den er satt.

## Ikke gjort (venter på B8 eller senere)

- **Innsending** via Altinn 3 med Maskinporten og systembruker, innhenting av valideringsresultat fra Skatteetaten og tilbakemeldinger. Kontrakten blir sannsynligvis en adapter-app som svarer på en hendelse, som for bank og EHF. Feltet `altinnReference` og statusen `submitted` er satt av til det.
- **Krav og betalinger** (Skatteetatens API) og avstemming av krav mot hovedboken: krever samme adgang.
- Terminer som opprettes automatisk fra selskapets terminlengde, kompensasjon, omvendt avgiftsplikt, e-handel, uttak, tap på krav og justeringer (spesifikasjonslinjer og merknader), kategori `primaernaering` med årstermin utover periodekoden, og avgiftsfrie koder utover grunnlag/sats-reglene.
- Skatteetatens **testverktøy** er ikke kjørt (som for SAF-T); validering skjer mot skjemaet og de reglene vi kan lese ut av meldingen.
- Fristberegningen kjenner ikke til spesielle regler (for eksempel andre frister for primærnæring) eller utsettelser Skatteetaten gir i enkeltår.

## Konsekvenser

- En MVA-melding kan lages og lastes ned (filen ligger på posten), og leveres i Altinn av en person. Det er et fungerende fallback uten adgang, men «sendt mot testmiljø» gjenstår.
- Regnskapsfører bør gjennom koder, satser og avrunding før pilot (B2): hele kroner per linje kan gi differanse mot oppgjøret i øre, og dette er en bevisst forskjell.
