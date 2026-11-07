# Worlds Yet Unseen — Planetary Physics & Environmental Systems

**Stand:** 7. November 2026  
**Status:** Grundprinzip abgestimmt; Parameterumfang als Vorschlag zur gemeinsamen Entscheidung  
**Bezug:** [World Vision](World_Vision.md)

## 1. Verbindliches Grundprinzip

Die Physik in **Worlds Yet Unseen (WYU)** soll **glaubwürdig, konsistent und realistisch wirken**, aber für **Performance und Spielbarkeit gezielt vereinfacht** werden. Wissenschaftliche Plausibilität ist eine Leitlinie, keine Verpflichtung zur vollständigen numerischen Simulation aller Prozesse.

- **Groundwork** stellt generische Simulationssysteme und Schnittstellen bereit.
- **WYU** legt planetare Inhalte, konkrete Werte, Gameplay-Auswirkungen und Balancing fest.
- Simulationstiefe darf von Relevanz und Nähe zum Spieler abhängen, ohne offensichtliche Widersprüche zu erzeugen.
- Umweltparameter sollen die Erkundung, den Basisbau, Fahrzeuge und später die Raumfahrt nachvollziehbar beeinflussen.
- Freie Voxel-Geländeveränderung und unterirdischer Bau müssen bei späteren technischen Konzepten berücksichtigt werden.
- **Gefährliche Solarstürme bleiben ein spätes Spielthema** und werden nicht als früher Überlebensdruck eingeführt.

## 2. Vorgeschlagene Eigenschaften je Himmelskörper (noch nicht final)

| Gruppe | Mögliche Parameter | Beabsichtigte Wirkung |
| --- | --- | --- |
| Körper und Gravitation | Radius, Masse bzw. Gravitationsparameter, Rotationsdauer, Achsneigung | Schwerkraft, Sprung- und Fallverhalten, planetare Unterschiede, Tagesrhythmus |
| Atmosphäre | Vorhandensein, Druck, Dichteprofil, Zusammensetzung, Sauerstoff-Partialdruck, weitere Gase | Atmung, Luftwiderstand, Flugverhalten, Schutz- und Lebenserhaltungssysteme |
| Thermische Bedingungen | Basistemperatur, Tag-/Nacht-Schwankung, Höhen- und Breitengrad-Effekte | Spielerexposition, Maschinenbetrieb, Klima |
| Wasser und Oberflächen | Flüssigkeitsvorkommen und relevante lokale Oberflächenbedingungen | Umgebungen, Ressourcen, Auftrieb und mögliche Lebensräume |
| Strahlungsumgebung | Grundbelastung, planetarer Schutz, stellare Einflüsse | Spätere technische Schutzsysteme und langfristige Risiken |
| Bahnen und Astronomie | Umlaufparameter, Beleuchtung, Sternabstand soweit für Gameplay relevant | Jahreszeiten, Tageslicht, Himmelsansicht, Raumfahrtkontext |

**Fachliche Konsistenz:** Schwerkraft an der Oberfläche ergibt sich aus Masse und Radius und sollte nicht unabhängig davon beliebig konfiguriert werden. Für die Atembarkeit zählt nicht allein der Sauerstoff-Prozentanteil, sondern insbesondere sein Partialdruck im Zusammenhang mit dem Gesamtdruck.

## 3. Vorgeschlagene örtlich und zeitlich variable Bedingungen (noch nicht final)

- Temperatur, Wind, Niederschlag, Feuchte, Nebel und Wolken.
- Lokaler Luftdruck und Luftdichte, insbesondere abhängig von der Höhe.
- Unterschiede durch Biome, Topografie, Wasserflächen, Tageszeit und mögliche Jahreszeiten.
- Gewöhnliche Stürme und Unwetter unabhängig vom deutlich späteren Solarsturm-Thema.
- Lokale Auswirkungen auf Sicht, Bewegung, Bau, Energiebedarf und Technik.

## 4. Performance-Leitlinie

Die **Daten eines Himmelskörpers** bilden die stabile Ausgangslage. **Lokale Bedingungen** werden bei Bedarf für eine Region, eine Position oder ein aktives Objekt abgeleitet. Nicht jede entfernte Region benötigt in jedem Simulationsschritt eine hochaufgelöste Berechnung.

Mögliche technische Richtungen (zu prüfen, noch keine Architekturentscheidung):
- Parameter-/Feldmodelle statt vollständiger globaler Strömungs- und Atmosphärenphysik.
- Höhere Simulationsauflösung nur in aktuell relevanten Bereichen.
- Vereinfachte gravitative Modelle für das übliche Gameplay; aufwendigere Orbitalmodelle nur dort, wo es tatsächlich nötig ist.
- Statische strukturelle Prüfungen für Bauwerke und dynamische Physik primär bei Bewegung, Schäden oder Einsturz.
- Kontinuierlich wirkende Übergänge zwischen Oberfläche, Atmosphäre und Weltraum ohne notwendigen Anspruch auf vollständige Mehrkörper-Himmelsmechanik.

## 5. Offene Entscheidungen

1. Ist der Startplanet anfangs in seinen wichtigsten Regionen ohne Atemgerät bewohnbar?
2. Welche physikalischen Eigenschaften sollen tatsächlich von Planet zu Planet variieren und welche nur lokal?
3. Welchen Grad an realistischem Flug-/Orbitalverhalten soll die Raumfahrt erfordern?
4. Welche Auswirkungen von Temperatur, Sauerstoff, Druck und Strahlung sind spielrelevant?
5. Welche Mindestanforderungen gelten für Druck- und Sauerstoffsysteme in unterirdischen oder geschlossenen Basen?
6. Welche Simulationsbudgets, Genauigkeiten und Skalierungsgrenzen werden erst nach Prototypen festgelegt?

**Abgrenzung:** Dieses Dokument legt keine konkreten Zahlen für den Startplaneten, keine vollständige Liste aller Himmelskörper und keine endgültige Simulationsarchitektur fest. Die Vorschläge werden schrittweise mit dem Spielkonzept abgestimmt.
