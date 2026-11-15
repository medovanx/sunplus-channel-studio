# Sunplus DK channel database format

This describes the `*_db.bin` channel database used by Sunplus DK receivers, as read and written by `SunplusChannelStudio.Core`. All numbers are little-endian. Every record has a fixed size and the file has no padding or checksum.

The layout was recovered from the original VB6 editor's load and save routines and checked against real databases: the library round-trips them byte for byte.

## File layout

| Part | Size |
| --- | --- |
| Header | 24 bytes |
| Satellites | 36 bytes each |
| Transponders | 36 bytes each |
| TV channels | 92, 96 or 100 bytes each |
| Radio channels | same size as TV channels |

## Header (24 bytes)

| Offset | Type | Meaning |
| --- | --- | --- |
| 0 | char[4] | Magic `db15` |
| 4 | u32 | Unknown, kept as read |
| 8 | u32 | Transponder count |
| 12 | u32 | TV channel count |
| 16 | u32 | Radio channel count |
| 20 | u32 | Satellite count |

## Satellite (36 bytes)

| Offset | Type | Meaning |
| --- | --- | --- |
| 0 | byte[4] | Flags, kept as read |
| 4 | text[20] | Name |
| 24 | u32 | Orbital position in tenths of a degree (70 is 7.0) |
| 28 | u16 | LNB low oscillator, MHz |
| 30 | u16 | LNB high oscillator, MHz |
| 32 | u32 | Options, kept as read |

## Transponder (36 bytes)

| Offset | Type | Meaning |
| --- | --- | --- |
| 0 | text[16] | Name |
| 16 | u16 | Frequency, MHz |
| 18 | u16 | Symbol rate, kS/s |
| 20 | u16 | Satellite index |
| 22 | u16 | Transponder id |
| 24 | u8 | Polarization: 0 vertical, 1 horizontal |
| 25 | byte[11] | Kept as read |

## Channel

| Offset | Type | Meaning |
| --- | --- | --- |
| 0 | u8 | Flags, kept as read |
| 1 | u8 | Kind: 1 TV, 2 radio |
| 2 | text[42] | Name |
| 44 | u16 | Transponder index (position in the transponder list) |
| 46 | u16 | Service id |
| 48 | u16 | Transport stream id |
| 50 | u16 | Original network id |
| 52 | u16 | Favourite group |
| 54 | u16 | Definition: 0 SD, 1 HD, other values shown as TR |
| 56 | u16 | Video PID |
| 60 | u32 | Video stream type |
| 64 | u16 | Audio PID A |
| 68 | u32 | Audio A stream type |
| 72 | u16 | Audio PID B |
| 76 | u32 | Audio B stream type |
| 80 | u16 | PCR PID |
| 82 | u16 | PMT PID |
| 84 | byte[8] | Kept as read |

PIDs are 13 bits. The receiver keeps flags in the top three bits of the PID words, so mask with `0x1FFF` to read the PID. `0x1FFF` itself means no stream.

### Favourite groups

| Value | Group |
| --- | --- |
| 0x00 | All |
| 0x01 | News |
| 0x02 | Science |
| 0x04 | Sports |
| 0x08 | Movies |
| 0x10 | Kids |
| 0x20 | Music |
| 0x40 | Fashion |
| 0x80 | Financial |

Any other value is shown as no group.

### Database types

The three types differ only in the channel record size. Bytes 0 to 91 are the same in all of them; the multistream types add bytes at the end.

| Type | Channel record |
| --- | --- |
| Normal DK-OTP | 92 bytes |
| Multistream V1 | 96 bytes |
| Multistream V2 | 100 bytes |

The type can be told from the file size: subtract the header, satellites and transponders, then divide by the channel count.

## Text

Names follow DVB SI text rules (ETSI EN 300 468, annex A). A first byte below `0x20` selects the character table, for example `0x02` for ISO 8859-6 (Arabic). Without one, the text is Latin. Fields are zero padded and need at least one terminating zero.
