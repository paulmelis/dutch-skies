#!/usr/bin/env python
from math import sqrt, degrees, radians, sin, cos, exp
from random import random, randint, seed

seed(123456)

def line_point_distance(p, q, t):
    """Return closest distance between 2D point t
    and the line between points p and q"""
    
    x1 = p[0]
    y1 = p[1]
    
    x2 = q[0]
    y2 = q[1]
    
    x0 = t[0]
    y0 = t[1]
    
    num = abs((x2-x1)*(y1-y0) - (x1-x0)*(y2-y1))    
    den = sqrt((x2-x1)**2 + (y2-y1)**2)
    return num/den
    
def rot(p, angle_degrees):
    """Positive rotation = CCW"""
    a = radians(angle_degrees)
    cos_a = cos(a)
    sin_a = sin(a)
    
    qx = cos_a*p[0] - sin_a*p[1]
    qy = sin_a*p[0] + cos_a*p[1]

    return (qx, qy)
    
if False:
    print(line_point_distance((0,0), (1,0), (0.5,0)))
    print(line_point_distance((0,0), (1,0), (0.5,1)))
    
    print(line_point_distance((0,0), (1,1), (0.5,0.5)))
    print(line_point_distance((0,0), (1,1), (0.5,2)))

if False:
    print(rot((1,0), 90))
    print(rot((1,0), 45))
    print(rot((1,0), 0))
    
    print(rot((0.88,0.88), -45))
    
    
def compute_energy(tx, ty, r):
    """RMS"""
    
    e2 = 0.0
    for ob_origin, name, direction in OBSERVATIONS:
        #print(name)
        o2 = rot(ob_origin, r)
        o2 = (o2[0] + tx, o2[1] + ty)
        p2 = rot((ob_origin[0]+direction[0], ob_origin[1]+direction[1]), r)
        p2 = (p2[0]+tx, p2[1]+ty)
        #print(ob_origin, '->', o2)
        #print(direction, '->', p2)
        dist = line_point_distance(o2, p2, GROUND_TRUTH[name])
        #print(dist)
        e2 += dist*dist
        
    return sqrt(e2)
    
def rand_unit():
    return (random()-0.5)*2
    
if __name__ == '__main__':

    # Ground truth (in coord system to align to)
    # Point coordinates
    GROUND_TRUTH = {
        'A': (3, 6), 'B': (-7, -11), 'C': (2, -3), 'D': (-6, 8)
    }
    
    tx = -2.9
    ty = -3.3
    r = -25.06
    
    for name, p in GROUND_TRUTH.items():
        p = (p[0]-tx, p[1]-ty)
        p = rot(p, -r)        
        print(name, p)
    
    # Direction vectors from observer position
    OBSERVATIONS = [
        ((0,0), 'A', (1.7, 10.8)),
        ((-2.5,1.5), 'A', (3.9, 9.9)),
        ((0,0), 'B', (-0.6, -8.7)), 
        ((0,0), 'C', (4.4, 2.3)), 
        ((0,0), 'D', (-7.5, 9))
    ]
    
    #print(compute_energy(tx, ty, r))
    #print(compute_energy(tx, ty, r+90))
    #print(compute_energy(tx, ty, r+180))    
    #print(compute_energy(tx, ty, r+270))
    #print(compute_energy(-2.972659, -3.278703, 155.837377-180))
    #print(compute_energy(-2.972659, -3.278703, 155.837377))    
    
    tx = 0.0
    ty = 0.0
    r = 0.0
        
    best_tx = tx
    best_ty = ty
    best_r = r
    best_energy = compute_energy(tx, ty, r)
    print('Energy %.6f (initial) | tx=%.6f, ty=%.6f, r=%.6f' % \
        (best_energy, best_tx, best_ty, best_r))
    
    K = 20000
    for k in range(0, K):
        T = 1 - (k+1)/K
        #print('Iter %d, T = %.6f' % (k, T))
        
        # Pick neighbour
        can_tx = tx
        can_ty = ty
        can_r = r
        
        idx = randint(0, 2)
        if idx == 0:
            #print('Mutating tx')            
            can_tx = tx + rand_unit()*200*T
        elif idx == 1:
            #print('Mutating ty')
            can_ty = ty + rand_unit()*200*T
        elif idx == 2:
            #print('Mutating r')
            can_r = (r + rand_unit()*360*T) % 360
            
        energy = compute_energy(can_tx, can_ty, can_r)
        #print('Energy %.6f (candidate) | can_tx=%.6f, can_ty=%.6f, can_r=%.6f' % \
        #    (energy, can_tx, can_ty, can_r))
            
        if energy <= best_energy:
            print('[%d] Energy %.6f (new best) | tx=%.6f, ty=%.6f, r=%.6f' % \
                (k, energy, can_tx, can_ty, can_r))
            best_energy = energy
            best_tx = can_tx
            best_ty = can_ty
            best_r = can_r
        elif T > 0:
            #print(energy, best_energy, T)
            p = exp(-(energy-best_energy)/T)
            if random() <= p:
                #print('Transitioning to less optimal solution')
                tx = can_tx
                ty = can_ty
                r = can_r
                
    print('Best: tx %.6f, ty %.6f, r %.6f (energy %.6f)' % \
        (best_tx, best_ty, best_r, best_energy))
        
    
