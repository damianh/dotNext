---
name: dotNext Raft Explainers
description: Interactive protocol lessons presented as precise typographic specimens.
colors:
  signal-blue: "#1748d1"
  signal-blue-deep: "#0e339d"
  paper: "#f4f3ef"
  surface: "#ecebe7"
  ink: "#111111"
  muted-ink: "#5d5d5a"
  rule: "#b9b8b3"
  soft-rule: "#d8d7d2"
  danger: "#a52a2a"
  white: "#ffffff"
  quiet-ink: "#898985"
  selected-muted: "#cdd9ff"
  disabled: "#cfceca"
  disabled-ink: "#747470"
  success: "#258f45"
  warning: "#fff2b8"
typography:
  code:
    fontFamily: "Segoe UI Variable Text, Aptos Mono, ui-monospace, Cascadia Mono, monospace"
    fontSize: "0.92em"
    fontWeight: 400
    lineHeight: 1.5
    letterSpacing: "normal"
  display:
    fontFamily: "Segoe UI Variable Display, Arial Narrow, Arial, sans-serif"
    fontSize: "clamp(4.8rem, 11vw, 10.5rem)"
    fontWeight: 520
    lineHeight: 0.78
    letterSpacing: "-0.065em"
  heading:
    fontFamily: "Segoe UI Variable Display, Arial Narrow, Arial, sans-serif"
    fontSize: "clamp(2rem, 4vw, 4.25rem)"
    fontWeight: 520
    lineHeight: 0.95
    letterSpacing: "-0.055em"
  body:
    fontFamily: "Segoe UI Variable Text, Aptos Mono, ui-monospace, Cascadia Mono, monospace"
    fontSize: "1rem"
    fontWeight: 400
    lineHeight: 1.5
    letterSpacing: "normal"
  label:
    fontFamily: "Segoe UI Variable Text, Aptos Mono, ui-monospace, Cascadia Mono, monospace"
    fontSize: "0.65rem"
    fontWeight: 400
    lineHeight: 1.25
    letterSpacing: "normal"
  data:
    fontFamily: "Segoe UI Variable Text, Aptos Mono, ui-monospace, Cascadia Mono, monospace"
    fontSize: "0.75rem"
    fontWeight: 400
    lineHeight: 1.25
    letterSpacing: "normal"
  title-small:
    fontFamily: "Segoe UI Variable Display, Arial Narrow, Arial, sans-serif"
    fontSize: "1.15rem"
    fontWeight: 520
    lineHeight: 1.5
    letterSpacing: "normal"
  title:
    fontFamily: "Segoe UI Variable Display, Arial Narrow, Arial, sans-serif"
    fontSize: "1.25rem"
    fontWeight: 520
    lineHeight: 1.45
    letterSpacing: "normal"
  lead:
    fontFamily: "Segoe UI Variable Display, Arial Narrow, Arial, sans-serif"
    fontSize: "clamp(1.05rem, 1.6vw, 1.35rem)"
    fontWeight: 400
    lineHeight: 1.45
    letterSpacing: "normal"
  specimen:
    fontFamily: "Segoe UI Variable Display, Arial Narrow, Arial, sans-serif"
    fontSize: "clamp(9rem, 17vw, 17rem)"
    fontWeight: 330
    lineHeight: 0.75
    letterSpacing: "-0.09em"
  verdict-mark:
    fontFamily: "Segoe UI Variable Display, Arial Narrow, Arial, sans-serif"
    fontSize: "clamp(5rem, 10vw, 9rem)"
    fontWeight: 300
    lineHeight: 0.7
    letterSpacing: "normal"
  icon:
    fontFamily: "Segoe UI Variable Display, Arial Narrow, Arial, sans-serif"
    fontSize: "2rem"
    fontWeight: 300
    lineHeight: 1
    letterSpacing: "normal"
  specimen-tablet:
    fontFamily: "Segoe UI Variable Display, Arial Narrow, Arial, sans-serif"
    fontSize: "clamp(7rem, 19vw, 12rem)"
    fontWeight: 330
    lineHeight: 0.75
    letterSpacing: "-0.09em"
  display-mobile:
    fontFamily: "Segoe UI Variable Display, Arial Narrow, Arial, sans-serif"
    fontSize: "clamp(4.5rem, 22vw, 7rem)"
    fontWeight: 520
    lineHeight: 0.78
    letterSpacing: "-0.065em"
  specimen-mobile:
    fontFamily: "Segoe UI Variable Display, Arial Narrow, Arial, sans-serif"
    fontSize: "9rem"
    fontWeight: 330
    lineHeight: 0.75
    letterSpacing: "-0.09em"
  verdict-mobile:
    fontFamily: "Segoe UI Variable Display, Arial Narrow, Arial, sans-serif"
    fontSize: "5rem"
    fontWeight: 300
    lineHeight: 0.7
    letterSpacing: "normal"
rounded:
  none: "0"
spacing:
  tight: "0.75rem"
  control: "1.25rem"
  section: "clamp(4rem, 8vw, 7rem)"
components:
  button-primary:
    backgroundColor: "{colors.signal-blue}"
    textColor: "#ffffff"
    rounded: "{rounded.none}"
    padding: "0 1.5rem"
    height: "5.5rem"
  button-primary-hover:
    backgroundColor: "{colors.signal-blue-deep}"
    textColor: "#ffffff"
  specimen-selected:
    backgroundColor: "{colors.signal-blue}"
    textColor: "#ffffff"
    rounded: "{rounded.none}"
    padding: "1.25rem"
---

# Design System: dotNext Raft Explainers

## Overview

**Creative North Star: "The Protocol Specimen"**

The explainers borrow the rigor of an interactive variable-type specimen: paper, black ink, one signal
blue, hairline rules, and dramatic shifts in typographic weight. Protocol state is the specimen. A role
change should alter the visual weight of a node as clearly as an axis change alters a glyph.

The system is flat, exact, and instructional rather than decorative. The learner's task always outranks
expression, but the mechanism itself should dominate the composition at poster scale.

**Key Characteristics:**
- Paper-white fields with black one-pixel rules.
- Signal blue reserved for active choices, confirmed state, focus, and causality.
- Large variable display type paired with compact data labels.
- Square, ruled controls with no ornamental containers.
- Text equivalents for every color or motion change.

## Colors

The strategy is restrained: neutral paper and ink carry the page; one saturated blue identifies live state.

### Primary
- **Signal Blue** (#1748d1): Selected predictions, leaders, active navigation, focus, and causal arrows.
- **Deep Signal Blue** (#0e339d): Primary-action hover and code emphasis.

### Neutral
- **Paper** (#f4f3ef): Page ground.
- **Surface** (#ecebe7): Quiet disabled or secondary fields.
- **Ink** (#111111): Primary type and structural rules.
- **Muted Ink** (#5d5d5a): Explanatory text and inactive metadata.
- **Rule** (#b9b8b3): Component boundaries.
- **Soft Rule** (#d8d7d2): Trace rows and loading placeholders.

### Named Rules

**The Signal Means State Rule.** Blue is never decoration; it marks an action, selection, focus, protocol
outcome, or causal direction.

## Typography

**Display Font:** Segoe UI Variable Display (with Arial Narrow and Arial fallback)  
**Body Font:** Segoe UI Variable Text (with Aptos Mono, ui-monospace, and Cascadia Mono fallback)

**Character:** Display type behaves like a specimen axis: condensed and light while unresolved, wider and
heavier when a role becomes authoritative. Compact mono-like labels provide measurement and provenance.

### Hierarchy
- **Display** (520, clamp(4.8rem, 11vw, 10.5rem), 0.78): One question or protocol idea per first viewport.
- **Heading** (520, clamp(2rem, 4vw, 4.25rem), 0.95): Experiment and evidence sections.
- **Body** (400, 1rem, 1.5): Explanations, kept near 65ch where practical.
- **Label** (400, 0.65rem, uppercase): Axis readouts, state, sequence, and provenance.

### Named Rules

**The State Has Weight Rule.** Use weight and width changes for protocol authority; do not substitute badges,
glows, or decorative icons.

## Layout

The desktop canvas is a wide ruled sheet capped at 1600px with fluid side gutters. First viewports pair a
poster-scale mechanism with a narrow evidence column. Experiments use equal columns so peers remain visually
equal before the reveal. Sections are separated by black rules and generous vertical intervals.

Below 900px, the opening becomes a single column. Below 680px, node lineups become horizontal, full-width
scroll-snap pages; the reveal remains withheld until a prediction is committed. Controls stack without
changing their order or labels.

## Elevation & Depth

The system uses no shadows. Hierarchy comes from scale, ruled boundaries, neutral fields, and blue state
changes. Focus uses a three-pixel blue inset outline rather than elevation.

## Shapes

Containers and controls are square. One-pixel rules form every boundary. Circles are limited to native radio
controls and axis-position markers because those shapes encode a selection or measured point.

## Components

### Buttons
- **Shape:** Rectangular with no radius.
- **Primary:** Signal blue with white uppercase text and an arrow at the far edge.
- **Hover / Focus:** Deep blue on hover; three-pixel blue focus outline. Disabled state becomes gray and keeps its label.
- **Secondary:** Paper background, black ink, and a ruled boundary.

### Cards / Containers
- **Corner Style:** No radius.
- **Background:** Paper at rest; signal blue when selected or authoritative.
- **Shadow Strategy:** None.
- **Border:** One-pixel rule.
- **Internal Padding:** 1.25rem.

### Inputs / Fields
- **Style:** Native radio semantics with a signal-blue accent; node choices expose the full label as the hit target.
- **Focus:** Three-pixel inset blue outline around the specimen.
- **Error / Disabled:** Errors use danger ink with an explicit recovery action; disabled controls use neutral gray.

### Navigation

Desktop navigation occupies ruled cells; the active destination gets blue type and a three-pixel bottom rule.
On narrow screens, secondary destinations disappear while brand and source remain.

### Node Specimen

Each peer is an equal ruled cell with a large initial, name, role, term, and axis line. Before commitment no
cell hints at the outcome. Selection floods one cell blue. Reveal uses heavier, wider type for the leader and
lighter type for followers, with textual roles always present.

## Do's and Don'ts

### Do:
- **Do** ask for a prediction before revealing state.
- **Do** connect every blue mark to a semantic state or action.
- **Do** keep peer nodes equal until the protocol produces a distinction.
- **Do** expose causal events as ordered text.

### Don't:
- **Don't** wrap lessons in generic rounded cards.
- **Don't** use neon, gradients, glows, or code-themed decoration.
- **Don't** use color or motion as the only state signal.
- **Don't** turn compact data labels into the primary reading voice.
