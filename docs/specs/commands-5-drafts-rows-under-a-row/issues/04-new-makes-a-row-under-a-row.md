# 04: `new` makes a row under a row

**What to build:** A described product may say the table its entities are rows of and the term that names the row
above; `new <product> <row above> <terms>` reads its first word as that row and writes it into the terms by name and
whole key (ADR-0016 note 48).

**Pull request:** 5a

**Blocked by:** 01.

**Status:** done

- [x] The first word after the product's words is the row above where it names one, whole or by how it ends, and the
      request names it by its whole key.
- [x] The term typed by its name (or one of its other names) names the row above as the first word does, whole or
      by how it ends, and is written with its whole key. *Corrected in review: "read as any term" left
      `trade=E00004` refused where `E00004` was read.*
- [x] With neither, the product is refused as missing the term.
- [x] A product that says a table its application does not register, or a term it has not, fails at start, by
      name; one whose table is under no table fails where its terms are first read.
