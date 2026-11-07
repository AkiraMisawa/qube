# 05 The XVA request's KVA option returns spot capital

Status: ready-for-agent

ADR-0013 §2 and §6: the fake XVA provider's KVA option computes spot capital through the reference implementation and
returns its trace, a capital profile and KVA, marked simulated. The synthetic sources gain the fields SA-CCR reads.
The measure registers into the page with its membership, inputs, trade columns and validation.
